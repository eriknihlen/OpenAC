#version 460 core
#extension GL_EXT_multiview : require

#include "directional_shadow_common.glsl"

layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in uvec4 aPacked0;
layout(location = 3) in uvec4 aPacked1;
layout(location = 4) in uvec4 aPacked2;
layout(location = 5) in uvec4 aPacked3;

void main() {
    gl_Position = uShadowWorldToClip[int(gl_ViewIndex)] * vec4(aPosition, 1.0);
}
