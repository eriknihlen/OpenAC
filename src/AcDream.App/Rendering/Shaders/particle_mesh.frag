#version 430 core
#extension GL_ARB_bindless_texture : require

in vec2 vTexCoord;
in vec4 vColor;
out vec4 fragColor;

uniform uint uTextureIndexA;
uniform float uParamA;

void main() {
    vec4 color = ACDREAM_SAMPLE_ARRAY(uTextureIndexA, vec3(vTexCoord, uParamA)) * vColor;
    if (color.a < 0.02)
        discard;
    fragColor = color;
}
