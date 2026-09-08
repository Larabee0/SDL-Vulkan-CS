#version 460

layout(location = 0) in vec2 texCoord;
layout(location = 0) out vec4 color;
layout(location = 1) out vec4 color2;

layout (set = 0, binding = 0) uniform sampler2D srcBloomTexture;
layout (set = 0, binding = 1) uniform sampler2D srcMainTexture;

layout (set = 0, binding = 2) uniform Constants{
    float bloomStrength;
} constants;

vec3 computeBloomMix()
{
    vec2 coord = texCoord;
    coord.y = 1-texCoord.y;
    vec3 hdr = texture(srcMainTexture, coord).rgb;
    vec3 blm = texture(srcBloomTexture, coord).rgb;
    vec3 col = mix(hdr, blm, vec3(constants.bloomStrength));
    return col;
}

void main()
{
    vec3 col = computeBloomMix();
    //col = tonemap(col);
    color = vec4(col, 1.0f);
    color2 = vec4(0.0);
}