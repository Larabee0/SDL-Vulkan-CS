#version 460

#extension GL_ARB_shading_language_include : require
#include "smaa_defines.glsl"
#include "smaa_functions.glsl"

#define SMAA_EDGES_COLOR
#define GAMMA_FOR_EDGE_DETECTION (1/2.2)

layout (location = 0) in vec2 vTexCoord0;
layout (location = 1) in vec4 vOffset[3];
layout (location = 0) out vec4 outFragColour;

layout (set = 0, binding = 0) uniform sampler uSampler;
#if defined(SMAA_EDGES_DEPTH) || SMAA_PREDICATION
layout (set = 0, binding = 1) uniform texture2D uDepthTexture;
#endif

#if !defined(SMAA_EDGES_DEPTH) && SMAA_PREDICATION
layout (set = 0, binding = 2) uniform texture2D uColourTexture;
#elif !defined(SMAA_EDGES_DEPTH) && !SMAA_PREDICATION
layout (set = 0, binding = 1) uniform texture2D uColourTexture;
#endif

#ifdef SMAA_EDGES_LUMA
/**
 * Luma Edge Detection
 *
 * IMPORTANT NOTICE: luma edge detection requires gamma-corrected colours, and
 * thus 'colourTex' should be a non-sRGB texture.
 */
vec2 SMAALumaEdgeDetectionPS(vec2 texcoord,
                               vec4 offset[3],
                               sampler2D colourTex
                               #if SMAA_PREDICATION
                               , sampler2D predicationTex
                               #endif
                               ) {
    // Calculate the threshold:
    #if SMAA_PREDICATION
    vec2 threshold = SMAACalculatePredicatedThreshold(texcoord, offset, SMAATexturePass2D(predicationTex));
    #else
    vec2 threshold = vec2(SMAA_THRESHOLD, SMAA_THRESHOLD);
    #endif

    // Calculate lumas:
    vec3 weights = vec3(0.2126, 0.7152, 0.0722);
    float L = dot(texture(colourTex, texcoord).rgb, weights);

    float Lleft = dot(texture(colourTex, offset[0].xy).rgb, weights);
    float Ltop  = dot(texture(colourTex, offset[0].zw).rgb, weights);

    // We do the usual threshold:
    vec4 delta;
    delta.xy = abs(L - vec2(Lleft, Ltop));
    vec2 edges = step(threshold, delta.xy);

    // Then discard if there is no edge:
    if (dot(edges, vec2(1.0, 1.0)) == 0.0)
        discard;

    // Calculate right and bottom deltas:
    float Lright = dot(texture(colourTex, offset[1].xy).rgb, weights);
    float Lbottom  = dot(texture(colourTex, offset[1].zw).rgb, weights);
    delta.zw = abs(L - vec2(Lright, Lbottom));

    // Calculate the maximum delta in the direct neighborhood:
    vec2 maxDelta = max(delta.xy, delta.zw);

    // Calculate left-left and top-top deltas:
    float Lleftleft = dot(texture(colourTex, offset[2].xy).rgb, weights);
    float Ltoptop = dot(texture(colourTex, offset[2].zw).rgb, weights);
    delta.zw = abs(vec2(Lleft, Ltop) - vec2(Lleftleft, Ltoptop));

    // Calculate the final maximum delta:
    maxDelta = max(maxDelta.xy, delta.zw);
    float finalDelta = max(maxDelta.x, maxDelta.y);

    // Local contrast adaptation:
    edges.xy *= step(finalDelta, SMAA_LOCAL_CONTRAST_ADAPTATION_FACTOR * delta.xy);

    return edges;
}
#endif

#ifdef SMAA_EDGES_COLOR
/**
 * Colour Edge Detection
 *
 * IMPORTANT NOTICE: edges edge detection requires gamma-corrected colours, and
 * thus 'colourTex' should be a non-sRGB texture.
 */
vec2 SMAAColourEdgeDetectionPS(vec2 texcoord,
                                vec4 offset[3],
                                sampler texSampler,
                                texture2D colourTex
                                #if SMAA_PREDICATION
                                , sampler2D predicationTex
                                #endif
                                ) {
    // Calculate the threshold:
    #if SMAA_PREDICATION
    vec2 threshold = SMAACalculatePredicatedThreshold(texcoord, offset, predicationTex);
    #else
    vec2 threshold = vec2(SMAA_THRESHOLD, SMAA_THRESHOLD);
    #endif

    // Calculate edges deltas:
    vec4 delta;
    vec3 C = PositivePow(texture(sampler2D(colourTex, texSampler), texcoord).rgb,GAMMA_FOR_EDGE_DETECTION);

    vec3 Cleft =  PositivePow(texture(sampler2D(colourTex, texSampler), offset[0].xy).rgb,GAMMA_FOR_EDGE_DETECTION);
    vec3 t = abs(C - Cleft);
    delta.x = max(max(t.r, t.g), t.b);

    vec3 Ctop  =  PositivePow(texture(sampler2D(colourTex, texSampler), offset[0].zw).rgb,GAMMA_FOR_EDGE_DETECTION);
    t = abs(C - Ctop);
    delta.y = max(max(t.r, t.g), t.b);

    // We do the usual threshold:
    vec2 edges = step(threshold, delta.xy);

    // Then discard if there is no edge:
    if (dot(edges, vec2(1.0, 1.0)) == 0.0)
        discard;

    // Calculate right and bottom deltas:
    vec3 Cright =  PositivePow(texture(sampler2D(colourTex, texSampler), offset[1].xy).rgb,GAMMA_FOR_EDGE_DETECTION);
    t = abs(C - Cright);
    delta.z = max(max(t.r, t.g), t.b);

    vec3 Cbottom  =  PositivePow(texture(sampler2D(colourTex, texSampler), offset[1].zw).rgb,GAMMA_FOR_EDGE_DETECTION);
    t = abs(C - Cbottom);
    delta.w = max(max(t.r, t.g), t.b);

    // Calculate the maximum delta in the direct neighborhood:
    vec2 maxDelta = max(delta.xy, delta.zw);

    // Calculate left-left and top-top deltas:
    vec3 Cleftleft  =  PositivePow(texture(sampler2D(colourTex, texSampler), offset[2].xy).rgb,GAMMA_FOR_EDGE_DETECTION);
    t = abs(C - Cleftleft);
    delta.z = max(max(t.r, t.g), t.b);

    vec3 Ctoptop =  PositivePow(texture(sampler2D(colourTex, texSampler), offset[2].zw).rgb,GAMMA_FOR_EDGE_DETECTION);
    t = abs(C - Ctoptop);
    delta.w = max(max(t.r, t.g), t.b);

    // Calculate the final maximum delta:
    maxDelta = max(maxDelta.xy, delta.zw);
    float finalDelta = max(maxDelta.x, maxDelta.y);

    // Local contrast adaptation:
    edges.xy *= step(finalDelta, SMAA_LOCAL_CONTRAST_ADAPTATION_FACTOR * delta.xy);

    return edges;
}
#endif

#ifdef SMAA_EDGES_DEPTH
/**
 * Depth Edge Detection
 */
vec2 SMAADepthEdgeDetectionPS(vec2 texcoord, 
                                vec4 rtInfo,
                                vec4 offset[3],
                                sampler2D depthTex) {
    vec3 neighbours = SMAAGatherNeighbours(texcoord, rtInfo, offset, depthTex);
    vec2 delta = abs(neighbours.xx - vec2(neighbours.y, neighbours.z));
    vec2 edges = step(SMAA_DEPTH_THRESHOLD, delta);

    if (dot(edges, vec2(1.0, 1.0)) == 0.0)
        discard;

    return edges;
}
#endif

void main()
{
    vec2 edges;

    #ifdef SMAA_EDGES_DEPTH
    edges = SMAADepthEdgeDetectionPS(vTexCoord0, vOffset, uDepthTexture);
    #elif defined(SMAA_EDGES_LUMA)
    edges= SMAALumaEdgeDetectionPS(vTexCoord0,vOffset,uColourTexture
    #if SMAA_PREDICATION
    ,uDepthTexture
    #endif
    );
    #elif defined(SMAA_EDGES_COLOR)
    edges = SMAAColourEdgeDetectionPS(vTexCoord0,vOffset,uSampler,uColourTexture
    #if SMAA_PREDICATION
    ,uDepthTexture
    #endif
    );
    #endif
    outFragColour = vec4(edges,0.0,0.0);
}