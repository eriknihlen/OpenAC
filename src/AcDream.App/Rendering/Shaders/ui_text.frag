#version 430 core
#extension GL_ARB_bindless_texture : require
in vec2 vUv;
in vec4 vColor;
out vec4 FragColor;

uniform uint uTextureIndexA;
uniform uint uTextureIndexB;

// GpuTextureSlot.Unassigned. It is a loud sentinel precisely so it can be
// tested for rather than silently resolving to slot 0 — the failure mode that
// produced the magenta 1x1 UI placeholder. Both branches below are guarded, so
// it never reaches a sampler.
const uint kUnassignedTextureSlot = 0xFFFFFFFFu;

void main() {
    if (uTextureIndexB != kUnassignedTextureSlot) {
        float coverage = ACDREAM_SAMPLE_2D(uTextureIndexB, vUv).r;
        FragColor = vec4(vColor.rgb, vColor.a * coverage);
    } else if (uTextureIndexA != kUnassignedTextureSlot) {
        // RGBA dat sprite (decoded to RGBA8); modulate by tint/alpha.
        FragColor = ACDREAM_SAMPLE_2D(uTextureIndexA, vUv) * vColor;
    } else {
        FragColor = vColor;
    }
    if (FragColor.a < 0.005) discard;
}
