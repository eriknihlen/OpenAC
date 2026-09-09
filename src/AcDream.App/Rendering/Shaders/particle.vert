#version 430 core

// Unit quad vertex (XY -0.5..+0.5)
layout(location = 0) in vec2 aQuad;
layout(location = 1) in vec2 aTex;

layout(location = 2) in vec4 aCenter;
layout(location = 3) in vec4 aAxisX;
layout(location = 4) in vec4 aAxisY;
layout(location = 5) in vec4 aColor;
layout(location = 6) in uint aTextureIndex;
layout(location = 7) in uint aClipSlot;

uniform mat4 uViewProjection;

out vec2 vTex;
out vec4 vColor;
flat out uint vTextureIndex;

void main() {
    vec3 world = aCenter.xyz
               + aAxisX.xyz * aQuad.x
               + aAxisY.xyz * aQuad.y;

    vTex = aTex;
    vColor = aColor;
    vTextureIndex = aTextureIndex;
    gl_Position = uViewProjection * vec4(world, 1.0);
}
