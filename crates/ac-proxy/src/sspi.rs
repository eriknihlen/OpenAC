//! NTLM and Negotiate through the Windows security packages (SSPI), as
//! .NET's `NegotiateAuthentication` runs them for a client on Windows:
//! outbound credentials of the package (the logon's, or a user name,
//! domain and password packed by `SspiEncodeStringsAsAuthIdentity`), then
//! `InitializeSecurityContext` once per leg with the connection flag only
//! (no integrity or delegation for a proxy), the TLS channel binding when
//! the proxy is spoken to over TLS, and `CompleteAuthToken` where the
//! package asks for it.

#![allow(unsafe_code)]

use std::ptr;

use windows_sys::Win32::Foundation::{
    SEC_E_OK, SEC_I_COMPLETE_AND_CONTINUE, SEC_I_COMPLETE_NEEDED, SEC_I_CONTINUE_NEEDED,
};
use windows_sys::Win32::Networking::WinSock::{
    ADDRINFOW, AI_CANONNAME, FreeAddrInfoW, GetAddrInfoW,
};
use windows_sys::Win32::Security::Authentication::Identity::{
    AcquireCredentialsHandleW, CompleteAuthToken, DeleteSecurityContext, FreeContextBuffer,
    FreeCredentialsHandle, ISC_REQ_ALLOCATE_MEMORY, ISC_REQ_CONNECTION, InitializeSecurityContextW,
    SECBUFFER_CHANNEL_BINDINGS, SECBUFFER_TOKEN, SECBUFFER_VERSION, SECPKG_CRED_OUTBOUND,
    SECURITY_NATIVE_DREP, SecBuffer, SecBufferDesc, SspiEncodeStringsAsAuthIdentity,
    SspiFreeAuthIdentity,
};
use windows_sys::Win32::Security::Credentials::SecHandle;

use crate::resolve::Credentials;

/// The `SEC_CHANNEL_BINDINGS` header before its application data.
const BINDINGS_HEADER: usize = 32;

fn wide(text: &str) -> Vec<u16> {
    text.encode_utf16().chain(Some(0)).collect()
}

/// One client handshake with a proxy.
pub(crate) struct Context {
    credential: SecHandle,
    context: Option<SecHandle>,
    target: Vec<u16>,
    bindings: Option<Vec<u8>>,
    authenticated: bool,
}

impl std::fmt::Debug for Context {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("Context")
            .field("authenticated", &self.authenticated)
            .finish_non_exhaustive()
    }
}

impl Context {
    /// Credentials of `package` (`NTLM` or `Negotiate`) for the service
    /// `target` (`HTTP/<proxy host>`); `endpoint` is the RFC 5929
    /// `tls-server-end-point` hash of a proxy spoken to over TLS.
    pub(crate) fn new(
        package: &str,
        credentials: &Credentials,
        target: &str,
        endpoint: Option<&[u8]>,
    ) -> Result<Self, String> {
        let package_name = wide(package);
        let mut identity: *mut core::ffi::c_void = ptr::null_mut();
        if let Credentials::Named { user, password, .. } = credentials {
            let user = wide(user);
            let password = wide(password);
            let domain = credentials.domain().map(wide);
            // SAFETY: the strings are NUL-terminated and live across the
            // call; the packed identity is freed below.
            let status = unsafe {
                SspiEncodeStringsAsAuthIdentity(
                    user.as_ptr(),
                    domain.as_ref().map_or(ptr::null(), |d| d.as_ptr()),
                    password.as_ptr(),
                    &raw mut identity,
                )
            };
            if status != SEC_E_OK {
                return Err(format!(
                    "the credentials could not be prepared for {package} ({status:#x})"
                ));
            }
        }
        let mut credential = SecHandle::default();
        let mut expiry = 0i64;
        // SAFETY: the package name lives across the call; no identity means
        // the logon's own credentials; `credential` is filled in and freed
        // in `drop` (only when the call succeeded).
        let status = unsafe {
            AcquireCredentialsHandleW(
                ptr::null(),
                package_name.as_ptr(),
                SECPKG_CRED_OUTBOUND,
                ptr::null(),
                identity,
                None,
                ptr::null(),
                &raw mut credential,
                &raw mut expiry,
            )
        };
        if !identity.is_null() {
            // SAFETY: allocated by SspiEncodeStringsAsAuthIdentity above,
            // copied by AcquireCredentialsHandleW, freed once.
            unsafe { SspiFreeAuthIdentity(identity) };
        }
        if status != SEC_E_OK {
            return Err(format!(
                "Windows has no {package} credentials for this user ({status:#x})"
            ));
        }
        let bindings = endpoint.map(|hash| {
            let data = [b"tls-server-end-point:".as_slice(), hash].concat();
            let mut bytes = vec![0u8; BINDINGS_HEADER];
            let length = u32::try_from(data.len()).unwrap_or(u32::MAX);
            // cbApplicationDataLength and dwApplicationDataOffset; the
            // initiator and acceptor addresses stay empty.
            bytes[24..28].copy_from_slice(&length.to_ne_bytes());
            bytes[28..32].copy_from_slice(&(BINDINGS_HEADER as u32).to_ne_bytes());
            bytes.extend_from_slice(&data);
            bytes
        });
        Ok(Self {
            credential,
            context: None,
            target: wide(target),
            bindings,
            authenticated: false,
        })
    }

    /// Whether the handshake finished on this side.
    pub(crate) fn is_authenticated(&self) -> bool {
        self.authenticated
    }

    /// The next token, given the proxy's (none on the first leg). `Err`
    /// when the package refuses (the handshake is over).
    pub(crate) fn step(&mut self, input: Option<&[u8]>) -> Result<Vec<u8>, String> {
        let mut input_token = input.map(<[u8]>::to_vec);
        let mut bindings = self.bindings.clone();
        let mut inputs = Vec::new();
        if let Some(token) = input_token.as_mut() {
            inputs.push(SecBuffer {
                cbBuffer: u32::try_from(token.len()).map_err(|_| "the token is too long")?,
                BufferType: SECBUFFER_TOKEN,
                pvBuffer: token.as_mut_ptr().cast(),
            });
        }
        if let Some(bindings) = bindings.as_mut() {
            inputs.push(SecBuffer {
                cbBuffer: u32::try_from(bindings.len()).unwrap_or(u32::MAX),
                BufferType: SECBUFFER_CHANNEL_BINDINGS,
                pvBuffer: bindings.as_mut_ptr().cast(),
            });
        }
        let input_desc = SecBufferDesc {
            ulVersion: SECBUFFER_VERSION,
            cBuffers: u32::try_from(inputs.len()).unwrap_or(0),
            pBuffers: inputs.as_mut_ptr(),
        };
        let mut output = SecBuffer {
            cbBuffer: 0,
            BufferType: SECBUFFER_TOKEN,
            pvBuffer: ptr::null_mut(),
        };
        let mut output_desc = SecBufferDesc {
            ulVersion: SECBUFFER_VERSION,
            cBuffers: 1,
            pBuffers: &raw mut output,
        };
        let mut new_context = self.context.unwrap_or_default();
        let mut attributes = 0u32;
        let mut expiry = 0i64;
        let current = self.context.as_ref().map_or(ptr::null(), ptr::from_ref);
        // SAFETY: the credential handle is valid; the context handle is the
        // one an earlier call made (or none on the first leg); the input
        // buffers live until the call returns; the output token is
        // allocated by the package and freed below.
        let status = unsafe {
            InitializeSecurityContextW(
                &raw const self.credential,
                current,
                self.target.as_ptr(),
                ISC_REQ_CONNECTION | ISC_REQ_ALLOCATE_MEMORY,
                0,
                SECURITY_NATIVE_DREP,
                if inputs.is_empty() {
                    ptr::null()
                } else {
                    &raw const input_desc
                },
                0,
                &raw mut new_context,
                &raw mut output_desc,
                &raw mut attributes,
                &raw mut expiry,
            )
        };
        let token = if output.pvBuffer.is_null() {
            Vec::new()
        } else {
            // SAFETY: the package allocated `cbBuffer` bytes at `pvBuffer`;
            // they are copied, then freed once.
            unsafe {
                let bytes = std::slice::from_raw_parts(
                    output.pvBuffer.cast::<u8>(),
                    output.cbBuffer as usize,
                )
                .to_vec();
                FreeContextBuffer(output.pvBuffer);
                bytes
            }
        };
        match status {
            SEC_E_OK
            | SEC_I_CONTINUE_NEEDED
            | SEC_I_COMPLETE_NEEDED
            | SEC_I_COMPLETE_AND_CONTINUE => {
                self.context = Some(new_context);
                if status == SEC_I_COMPLETE_NEEDED || status == SEC_I_COMPLETE_AND_CONTINUE {
                    let mut token_copy = token.clone();
                    let mut buffer = SecBuffer {
                        cbBuffer: u32::try_from(token_copy.len()).unwrap_or(0),
                        BufferType: SECBUFFER_TOKEN,
                        pvBuffer: token_copy.as_mut_ptr().cast(),
                    };
                    let desc = SecBufferDesc {
                        ulVersion: SECBUFFER_VERSION,
                        cBuffers: 1,
                        pBuffers: &raw mut buffer,
                    };
                    // SAFETY: the context was just made; the buffer lives
                    // across the call.
                    let completed =
                        unsafe { CompleteAuthToken(&raw const new_context, &raw const desc) };
                    if completed != SEC_E_OK {
                        return Err(format!("completing the token failed ({completed:#x})"));
                    }
                }
                self.authenticated = status == SEC_E_OK || status == SEC_I_COMPLETE_NEEDED;
                Ok(token)
            }
            error => Err(format!("the security package refused ({error:#x})")),
        }
    }
}

impl Drop for Context {
    fn drop(&mut self) {
        // SAFETY: both handles were made by the calls above and are freed
        // once.
        unsafe {
            if let Some(context) = self.context.take() {
                DeleteSecurityContext(&raw const context);
            }
            FreeCredentialsHandle(&raw const self.credential);
        }
    }
}

/// The host's canonical name from DNS, so a name that is an alias reaches
/// the service's real name (.NET `Dns.GetHostEntry(host).HostName`); an
/// IP address is used as it is. Called once a connection to the host is
/// open (Windows sockets are started).
pub(crate) fn canonical_name(host: &str) -> String {
    let bare = host.trim_start_matches('[').trim_end_matches(']');
    if bare.parse::<std::net::IpAddr>().is_ok() {
        return host.to_owned();
    }
    let name = wide(bare);
    let hints = ADDRINFOW {
        ai_flags: AI_CANONNAME as i32,
        ..ADDRINFOW::default()
    };
    let mut result: *mut ADDRINFOW = ptr::null_mut();
    // SAFETY: the name and hints live across the call; the result list is
    // read, then freed once.
    unsafe {
        if GetAddrInfoW(
            name.as_ptr(),
            ptr::null(),
            &raw const hints,
            &raw mut result,
        ) != 0
            || result.is_null()
        {
            return host.to_owned();
        }
        let canonical = (*result).ai_canonname;
        let mut text = None;
        if !canonical.is_null() {
            let mut length = 0;
            while *canonical.add(length) != 0 {
                length += 1;
            }
            text = Some(String::from_utf16_lossy(std::slice::from_raw_parts(
                canonical, length,
            )));
        }
        FreeAddrInfoW(result);
        text.filter(|t| !t.is_empty())
            .unwrap_or_else(|| host.to_owned())
    }
}
