#version 430 core
#extension GL_ARB_bindless_texture : require

in vec2 vTex;
in vec4 vColor;
flat in uint vTextureIndex;
out vec4 fragColor;

void main() {
    vec4 texel;
    if (vTextureIndex != ACDREAM_TEXTURE_NONE) {
        texel = ACDREAM_SAMPLE_ARRAY(vTextureIndex, vec3(vTex, 0.0));
    } else {
        vec2 d = vTex - vec2(0.5, 0.5);
        float r = length(d) * 2.0;
        float falloff = smoothstep(1.0, 0.4, r);
        texel = vec4(1.0, 1.0, 1.0, falloff);
    }

    vec4 color = texel * vColor;
    if (color.a < 0.02)
        discard;

    fragColor = color;
}
