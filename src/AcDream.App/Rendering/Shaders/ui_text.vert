#version 430 core
layout(location = 0) in vec2 aPos;     // screen pixels, origin top-left
layout(location = 1) in vec2 aUv;
layout(location = 2) in vec4 aColor;

uniform float uParamA;
uniform float uParamB;

out vec2 vUv;
out vec4 vColor;

void main() {
    vec2 ndc = vec2(
        aPos.x / uParamA * 2.0 - 1.0,
        1.0 - aPos.y / uParamB * 2.0);
    gl_Position = vec4(ndc, 0.0, 1.0);
    vUv = aUv;
    vColor = aColor;
}
