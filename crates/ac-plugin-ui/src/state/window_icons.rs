//! The dock icon a plugin window names for itself (`icon_file`), read when the
//! window registers. It follows the rules of a plugin's `icon.png`, so a file
//! is small enough to read at once, and a window carries what it found to its
//! dock slot. A file may have a light and a dark version beside it, each under
//! the same rules, and the dock draws the one for its theme when it is there.
//!
//! A file that cannot be used is logged once and leaves the slot to the next
//! icon in the dock's order. A missing version is not reported, and neither is
//! a missing named file while a version of it is there. Windows naming the same
//! unchanged file share its pixels, so the window uploads them once.

use super::PluginUi;
use crate::file_images::variant_path;
use crate::icon::PluginIcon;
use crate::model::WindowIcon;

impl PluginUi {
    /// What plugin `owner`'s folder holds for the dock icon `path`: none when
    /// it has no folder, this host reads no files, or nothing there can be used.
    pub(super) fn read_window_icon(&mut self, owner: &str, path: &str) -> Option<WindowIcon> {
        let directory = self.plugin_folders.get(owner)?.clone();
        let loader = self.file_images.clone()?;
        let named = loader.read_dock_icon(&directory, path);
        let refusal = named.as_ref().err().cloned();
        if let Some(reason) = &refusal {
            self.report_window_icon(owner, path, reason);
        }
        let named = named
            .ok()
            .flatten()
            .map(|icon| self.share_window_icon(owner, path, icon));
        // A path refused for what it is, such as one that leaves the folder,
        // has its versions refused for the same reason: said once.
        let version = |ui: &mut Self, light: bool| {
            let file = variant_path(path, light)?;
            match loader.read_dock_icon(&directory, &file) {
                Ok(icon) => icon.map(|icon| ui.share_window_icon(owner, &file, icon)),
                Err(reason) => {
                    if refusal.as_deref() != Some(reason.as_str()) {
                        ui.report_window_icon(owner, &file, &reason);
                    }
                    None
                }
            }
        };
        let light = version(self, true);
        let dark = version(self, false);
        let icon = WindowIcon { named, light, dark };
        if icon.is_empty() {
            if refusal.is_none() {
                self.report_window_icon(owner, path, "does not exist");
            }
            return None;
        }
        Some(icon)
    }

    /// `icon`, or the copy already held of `file` when its pixels are the same.
    fn share_window_icon(&mut self, owner: &str, file: &str, icon: PluginIcon) -> PluginIcon {
        let held = self
            .window_icons
            .entry(owner.to_owned())
            .or_default()
            .entry(file.to_owned())
            .or_insert_with(|| icon.clone());
        if *held != icon {
            *held = icon;
        }
        held.clone()
    }

    /// Logs why a window's own dock icon is not drawn, once per file and reason.
    fn report_window_icon(&mut self, owner: &str, path: &str, reason: &str) {
        let label = format!("{owner}/{}", path.replace('\\', "/"));
        if self.file_reported.insert(format!("{label}\n{reason}")) {
            self.reports.push((
                owner.to_owned(),
                format!("plugin icon '{label}' ignored: {reason}"),
            ));
        }
    }
}

#[cfg(test)]
mod tests;
