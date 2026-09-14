#version 430 core
// World markers: rings and strips a plugin places on the ground, drawn in
// the scene with the world's depth so walls and floors hide them.
layout(location = 0) in vec3 aPos;
layout(location = 1) in vec4 aColor;

uniform mat4 uViewProjection;

out vec4 vColor;

void main() {
    vColor = aColor;
    gl_Position = uViewProjection * vec4(aPos, 1.0);
}
