#version 460
precision highp float;

#extension GL_ARB_shading_language_include : require
#include "smaa_defines.glsl"
#include "smaa_functions.glsl"

layout (location = 0) in vec2 vTexCoord0;
layout (location = 1) in vec4 vOffset;
layout (location = 0) out vec4 outFragColour;

layout (set = 0, binding = 0) uniform sampler uSampler;
layout (set = 0, binding = 1) uniform texture2D uColourTexture;
layout (set = 0, binding = 2) uniform texture2D uBlendTexture;

#if SMAA_REPROJECTION                                  
layout (set = 0, binding = 3) uniform sampler2D uVelocityTexture;
#endif

layout(push_constant) uniform TexelSize 
{
	vec4 value;
} texelSize;

//-----------------------------------------------------------------------------
// Neighborhood Blending Pixel Shader (Third Pass)

vec4 SMAANeighborhoodBlendingPS(vec2 texcoord,
                                  vec4 rtInfo,
                                  vec4 offset,
                                  sampler texSampler,
                                  texture2D colourTex,
                                  texture2D blendTex
                                  #if SMAA_REPROJECTION
                                  , sampler2D velocityTex
                                  #endif
                                  ) {
    // Fetch the blending weights for current pixel:
    vec4 a;
    a.x =  texture(sampler2D(blendTex, texSampler), offset.xy).a; // Right
    a.y =  texture(sampler2D(blendTex, texSampler), offset.zw).g; // Top
    a.wz = texture(sampler2D(blendTex, texSampler), texcoord).xz; // Bottom / Left

    // Is there any blending weight with a value greater than 0.0?
    SMAA_BRANCH
    if (dot(a, vec4(1.0, 1.0, 1.0, 1.0)) < 1e-5) {
        vec4 colour = textureLod(sampler2D(colourTex, texSampler), texcoord, 0.0);

        #if SMAA_REPROJECTION
        vec2 velocity = SMAA_DECODE_VELOCITY(textureLod(velocityTex, texcoord, 0.0));

        // Pack velocity into the alpha channel:
        colour.a = sqrt(5.0 * length(velocity));
        #endif

        return colour;
    } else {
        bool h = max(a.x, a.z) > max(a.y, a.w); // max(horizontal) > max(vertical)

        // Calculate the blending offsets:
        vec4 blendingOffset = vec4(0.0, a.y, 0.0, a.w);
        vec2 blendingWeight = a.yw;
        SMAAMovc(bvec4(h, h, h, h), blendingOffset, vec4(a.x, 0.0, a.z, 0.0));
        SMAAMovc(bvec2(h, h), blendingWeight, a.xz);
        blendingWeight /= dot(blendingWeight, vec2(1.0, 1.0));

        // Calculate the texture coordinates:
        vec4 blendingCoord = fma(blendingOffset, vec4(rtInfo.xy, -rtInfo.xy), texcoord.xyxy);

        // We exploit bilinear filtering to mix current pixel with the chosen
        // neighbor:
        vec4 colour = blendingWeight.x * textureLod(sampler2D(colourTex, texSampler), blendingCoord.xy, 0.0);
        colour += blendingWeight.y * textureLod(sampler2D(colourTex, texSampler), blendingCoord.zw, 0.0);

        #if SMAA_REPROJECTION
        // Antialias velocity for proper reprojection in a later stage:
        vec2 velocity = blendingWeight.x * SMAA_DECODE_VELOCITY(textureLod(velocityTex, blendingCoord.xy, 0.0));
        velocity += blendingWeight.y * SMAA_DECODE_VELOCITY(textureLod(velocityTex, blendingCoord.zw, 0.0));

        // Pack velocity into the alpha channel:
        colour.a = sqrt(5.0 * length(velocity));
        #endif

        return colour;
    }
}

void main()
{
    outFragColour = SMAANeighborhoodBlendingPS(
        vTexCoord0,
        texelSize.value,
        vOffset,
        uSampler,
        uColourTexture,
        uBlendTexture
    #if SMAA_REPROJECTION
    ,uVelocityTexture
    #endif
    );
}