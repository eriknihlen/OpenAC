#version 430 core

layout(location = 0) out vec2 vUv;

void main()
{
    vec2 triangle = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
    gl_Position = vec4(triangle * 2.0 - 1.0, 0.0, 1.0);
    vUv = vec2(triangle.x, 1.0 - triangle.y);
}
