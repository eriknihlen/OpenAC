#version 430 core


layout(location = 0) in vec3 aPos;

uniform mat4  uViewProjection;
uniform int   uRenderPass;

layout(std140, ACDREAM_UBO_SET binding = 2) uniform TerrainClip {
    int  uTerrainClipCount;
    vec4 uTerrainClipPlanes[8];
};

out gl_PerVertex {
    vec4  gl_Position;
    float gl_ClipDistance[8];
};

void main()
{
    vec4 clipPos = uViewProjection * vec4(aPos, 1.0);
    for (int i = 0; i < 8; i++)
        gl_ClipDistance[i] = (i < uTerrainClipCount) ? dot(uTerrainClipPlanes[i], clipPos) : 1.0;

    if (uRenderPass == 1)
    {
        clipPos.z = clipPos.w * uintBitsToFloat(0x3F7FFFEFu);
    }
    gl_Position = clipPos;
}
