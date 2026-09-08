#version 450

#extension GL_ARB_shading_language_include : require
#include "smaa_defines.glsl"

#include "smaa_functions.glsl"
layout (location = 0) in vec2 vTexCoord0;
layout (location = 1) in vec2 vPixCoord0;
layout (location = 2) in vec4 vOffset[3];
layout (location = 0) out vec4 outFragColour;

layout (set = 0, binding = 0) uniform texture2D uAreaTexture;
layout (set = 0, binding = 1) uniform texture2D uSearchTexture;
layout (set = 0, binding = 2) uniform texture2D uEdgeTexture;
layout (set = 0, binding = 3) uniform sampler uSampler;

layout(push_constant) uniform TexelSize 
{
	vec4 value;
} texelSize;

#if !defined(SMAA_DISABLE_DIAG_DETECTION)

/**
 * Allows to decode two binary values from a bilinear-filtered access.
 */
vec2 SMAADecodeDiagBilinearAccess(vec2 e) {
    // Bilinear access for fetching 'e' have a 0.25 offset, and we are
    // interested in the R and G edges:
    //
    // +---G---+-------+
    // |   x o R   x   |
    // +-------+-------+
    //
    // Then, if one of these edge is enabled:
    //   Red:   (0.75 * X + 0.25 * 1) => 0.25 or 1.0
    //   Green: (0.75 * 1 + 0.25 * X) => 0.75 or 1.0
    //
    // This function will unpack the values (fma + mul + round):
    // wolframalpha.com: round(x * abs(5 * x - 5 * 0.75)) plot 0 to 1
    e.r = e.r * abs(5.0 * e.r - 5.0 * 0.75);
    return round(e);
}

vec4 SMAADecodeDiagBilinearAccess(vec4 e) {
    e.rb = e.rb * abs(5.0 * e.rb - 5.0 * 0.75);
    return round(e);
}

/**
 * These functions allows to perform diagonal pattern searches.
 */
vec2 SMAASearchDiag1(texture2D edgesTex, sampler texSampler, vec2 texcoord, vec4 rtInfo, vec2 dir, out vec2 e) {
    vec4 coord = vec4(texcoord, -1.0, 1.0);
    vec3 t = vec3(rtInfo.xy, 1.0);
    while (coord.z < float(SMAA_MAX_SEARCH_STEPS_DIAG - 1) &&
           coord.w > 0.9) {
        coord.xyz = fma(t, vec3(dir, 1.0), coord.xyz);
        e = textureLod(sampler2D(edgesTex, texSampler), coord.xy, 0.0).rg;
        coord.w = dot(e, vec2(0.5, 0.5));
    }
    return coord.zw;
}

vec2 SMAASearchDiag2(texture2D edgesTex, sampler texSampler, vec2 texcoord, vec4 rtInfo, vec2 dir, out vec2 e) {
    vec4 coord = vec4(texcoord, -1.0, 1.0);
    coord.x += 0.25 * rtInfo.x; // See @SearchDiag2Optimization
    vec3 t = vec3(rtInfo.xy, 1.0);
    while (coord.z < float(SMAA_MAX_SEARCH_STEPS_DIAG - 1) &&
           coord.w > 0.9) {
        coord.xyz = fma(t, vec3(dir, 1.0), coord.xyz);

        // @SearchDiag2Optimization
        // Fetch both edges at once using bilinear filtering:
        e = textureLod(sampler2D(edgesTex, texSampler), coord.xy, 0.0).rg;
        e = SMAADecodeDiagBilinearAccess(e);

        // Non-optimized version:
        // e.g = textureLod(edgesTex, coord.xy).g;
        // e.r = SMAASampleLevelZeroOffset(edgesTex, coord.xy, int2(1, 0)).r;

        coord.w = dot(e, vec2(0.5, 0.5));
    }
    return coord.zw;
}

/** 
 * Similar to SMAAArea, this calculates the area corresponding to a certain
 * diagonal distance and crossing edges 'e'.
 */
vec2 SMAAAreaDiag(texture2D areaTex, sampler texSampler, vec2 dist, vec2 e, float offset) {
    vec2 texcoord = fma(vec2(SMAA_AREATEX_MAX_DISTANCE_DIAG, SMAA_AREATEX_MAX_DISTANCE_DIAG), e, dist);

    // We do a scale and bias for mapping to texel space:
    texcoord = fma(SMAA_AREATEX_PIXEL_SIZE, texcoord, 0.5 * SMAA_AREATEX_PIXEL_SIZE);

    // Diagonal areas are on the second half of the texture:
    texcoord.x += 0.5;

    // Move to proper place, according to the subpixel offset:
    texcoord.y += SMAA_AREATEX_SUBTEX_SIZE * offset;

    // Do it!
    return textureLod(sampler2D(areaTex, texSampler), texcoord, 0.0).rg;
}

/**
 * This searches for diagonal patterns and returns the corresponding weights.
 */
vec2 SMAACalculateDiagWeights(texture2D edgesTex, texture2D areaTex, sampler texSampler, vec2 texcoord, vec4 rtInfo, vec2 e, vec4 subsampleIndices) {
    vec2 weights = vec2(0.0, 0.0);

    // Search for the line ends:
    vec4 d;
    vec2 end;
    if (e.r > 0.0) {
        d.xz = SMAASearchDiag1(edgesTex, texSampler, texcoord, rtInfo, vec2(-1.0,  1.0), end);
        d.x += float(end.y > 0.9);
    } else
        d.xz = vec2(0.0, 0.0);
    d.yw = SMAASearchDiag1(edgesTex, texSampler, texcoord, rtInfo, vec2(1.0, -1.0), end);

    SMAA_BRANCH
    if (d.x + d.y > 2.0) { // d.x + d.y + 1 > 3
        // Fetch the crossing edges:
        vec4 coords = fma(vec4(-d.x + 0.25, d.x, d.y, -d.y - 0.25), rtInfo.xyxy, texcoord.xyxy);
        vec4 c;
        c.xy = textureLodOffset(sampler2D(edgesTex,texSampler), coords.xy, 0.0, ivec2(-1,  0)).rg;
        c.zw = textureLodOffset(sampler2D(edgesTex,texSampler), coords.zw, 0.0, ivec2( 1,  0)).rg;
        c.yxwz = SMAADecodeDiagBilinearAccess(c.xyzw);

        // Non-optimized version:
        // vec4 coords = fma(vec4(-d.x, d.x, d.y, -d.y), rtInfo.xyxy, texcoord.xyxy);
        // vec4 c;
        // c.x = SMAASampleLevelZeroOffset(edgesTex, coords.xy, int2(-1,  0)).g;
        // c.y = SMAASampleLevelZeroOffset(edgesTex, coords.xy, int2( 0,  0)).r;
        // c.z = SMAASampleLevelZeroOffset(edgesTex, coords.zw, int2( 1,  0)).g;
        // c.w = SMAASampleLevelZeroOffset(edgesTex, coords.zw, int2( 1, -1)).r;

        // Merge crossing edges at each side into a single value:
        vec2 cc = fma(vec2(2.0, 2.0), c.xz, c.yw);

        // Remove the crossing edge if we didn't found the end of the line:
        SMAAMovc(bvec2(step(0.9, d.zw)), cc, vec2(0.0, 0.0));

        // Fetch the areas for this line:
        weights += SMAAAreaDiag(areaTex, texSampler, d.xy, cc, subsampleIndices.z);
    }

    // Search for the line ends:
    d.xz = SMAASearchDiag2(edgesTex, texSampler, texcoord, rtInfo, vec2(-1.0, -1.0), end);
    if (textureLodOffset(sampler2D(edgesTex, texSampler), texcoord, 0.0,ivec2(1, 0)).r > 0.0) {
        d.yw = SMAASearchDiag2(edgesTex, texSampler, texcoord, rtInfo, vec2(1.0, 1.0), end);
        d.y += float(end.y > 0.9);
    } else
        d.yw = vec2(0.0, 0.0);

    SMAA_BRANCH
    if (d.x + d.y > 2.0) { // d.x + d.y + 1 > 3
        // Fetch the crossing edges:
        vec4 coords = fma(vec4(-d.x, -d.x, d.y, d.y), rtInfo.xyxy, texcoord.xyxy);
        vec4 c;
        c.x  = textureLodOffset(sampler2D(edgesTex, texSampler), coords.xy, 0.0, ivec2(-1,  0)).g;
        c.y  = textureLodOffset(sampler2D(edgesTex, texSampler), coords.xy, 0.0, ivec2( 0, -1)).r;
        c.zw = textureLodOffset(sampler2D(edgesTex, texSampler), coords.zw, 0.0, ivec2( 1,  0)).gr;
        vec2 cc = fma(vec2(2.0, 2.0), c.xz, c.yw);

        // Remove the crossing edge if we didn't found the end of the line:
        SMAAMovc(bvec2(step(0.9, d.zw)), cc, vec2(0.0, 0.0));

        // Fetch the areas for this line:
        weights += SMAAAreaDiag(areaTex, texSampler, d.xy, cc, subsampleIndices.w).gr;
    }

    return weights;
}
#endif

//-----------------------------------------------------------------------------
// Horizontal/Vertical Search Functions

/**
 * This allows to determine how much length should we add in the last step
 * of the searches. It takes the bilinearly interpolated edge (see 
 * @PSEUDO_GATHER4), and adds 0, 1 or 2, depending on which edges and
 * crossing edges are active.
 */
float SMAASearchLength(texture2D searchTex, sampler texSampler, vec2 e, float offset) {
    // The texture is flipped vertically, with left and right cases taking half
    // of the space horizontally:
    vec2 scale = SMAA_SEARCHTEX_SIZE * vec2(0.5, -1.0);
    vec2 bias = SMAA_SEARCHTEX_SIZE * vec2(offset, 1.0);

    // Scale and bias to access texel centers:
    scale += vec2(-1.0,  1.0);
    bias  += vec2( 0.5, -0.5);

    // Convert from pixel coordinates to texcoords:
    // (We use SMAA_SEARCHTEX_PACKED_SIZE because the texture is cropped)
    scale *= 1.0 / SMAA_SEARCHTEX_PACKED_SIZE;
    bias *= 1.0 / SMAA_SEARCHTEX_PACKED_SIZE;

    // Lookup the search texture:
    return textureLod(sampler2D(searchTex, texSampler), fma(scale, e, bias),0.0).r;
}

/**
 * Horizontal/vertical search functions for the 2nd pass.
 */
float SMAASearchXLeft(texture2D edgesTex, texture2D searchTex, sampler texSampler, vec2 texcoord, vec4 rtInfo, float end) {
    /**
     * @PSEUDO_GATHER4
     * This texcoord has been offset by (-0.25, -0.125) in the vertex shader to
     * sample between edge, thus fetching four edges in a row.
     * Sampling with different offsets in each direction allows to disambiguate
     * which edges are active from the four fetched ones.
     */
    vec2 e = vec2(0.0, 1.0);
    while (texcoord.x > end && 
           e.g > 0.8281 && // Is there some edge not activated?
           e.r == 0.0) { // Or is there a crossing edge that breaks the line?
        e = textureLod(sampler2D(edgesTex, texSampler), texcoord, 0.0).rg;
        texcoord = fma(-vec2(2.0, 0.0), rtInfo.xy, texcoord);
    }

    float offset = fma(-(255.0 / 127.0), SMAASearchLength(searchTex, texSampler, e, 0.0), 3.25);
    return fma(rtInfo.x, offset, texcoord.x);

    // Non-optimized version:
    // We correct the previous (-0.25, -0.125) offset we applied:
    // texcoord.x += 0.25 * rtInfo.x;

    // The searches are bias by 1, so adjust the coords accordingly:
    // texcoord.x += rtInfo.x;

    // Disambiguate the length added by the last step:
    // texcoord.x += 2.0 * rtInfo.x; // Undo last step
    // texcoord.x -= rtInfo.x * (255.0 / 127.0) * SMAASearchLength(SMAATexturePass2D(searchTex), e, 0.0);
    // return fma(rtInfo.x, offset, texcoord.x);
}

float SMAASearchXRight(texture2D edgesTex, texture2D searchTex, sampler texSampler, vec2 texcoord, vec4 rtInfo, float end) {
    vec2 e = vec2(0.0, 1.0);
    while (texcoord.x < end && 
           e.g > 0.8281 && // Is there some edge not activated?
           e.r == 0.0) { // Or is there a crossing edge that breaks the line?
        e = textureLod(sampler2D(edgesTex, texSampler), texcoord,0.0).rg;
        texcoord = fma(vec2(2.0, 0.0), rtInfo.xy, texcoord);
    }
    float offset = fma(-(255.0 / 127.0), SMAASearchLength(searchTex, texSampler, e, 0.5), 3.25);
    return fma(-rtInfo.x, offset, texcoord.x);
}

float SMAASearchYUp(texture2D edgesTex, texture2D searchTex, sampler texSampler, vec2 texcoord, vec4 rtInfo, float end) {
    vec2 e = vec2(1.0, 0.0);
    while (texcoord.y > end && 
           e.r > 0.8281 && // Is there some edge not activated?
           e.g == 0.0) { // Or is there a crossing edge that breaks the line?
        e = textureLod(sampler2D(edgesTex, texSampler), texcoord,0.0).rg;
        texcoord = fma(-vec2(0.0, 2.0), rtInfo.xy, texcoord);
    }
    float offset = fma(-(255.0 / 127.0), SMAASearchLength(searchTex, texSampler, e.gr, 0.0), 3.25);
    return fma(rtInfo.y, offset, texcoord.y);
}

float SMAASearchYDown(texture2D edgesTex, texture2D searchTex, sampler texSampler, vec2 texcoord, vec4 rtInfo, float end) {
    vec2 e = vec2(1.0, 0.0);
    while (texcoord.y < end && 
           e.r > 0.8281 && // Is there some edge not activated?
           e.g == 0.0) { // Or is there a crossing edge that breaks the line?
        e = textureLod(sampler2D(edgesTex, texSampler), texcoord,0.0).rg;
        texcoord = fma(vec2(0.0, 2.0), rtInfo.xy, texcoord);
    }
    float offset = fma(-(255.0 / 127.0), SMAASearchLength(searchTex, texSampler, e.gr, 0.5), 3.25);
    return fma(-rtInfo.y, offset, texcoord.y);
}

/** 
 * Ok, we have the distance and both crossing edges. So, what are the areas
 * at each side of current edge?
 */
vec2 SMAAArea(texture2D areaTex, sampler texSampler, vec2 dist, float e1, float e2, float offset) {
    // Rounding prevents precision errors of bilinear filtering:
    vec2 texcoord = fma(vec2(SMAA_AREATEX_MAX_DISTANCE, SMAA_AREATEX_MAX_DISTANCE), round(4.0 * vec2(e1, e2)), dist);
    
    // We do a scale and bias for mapping to texel space:
    texcoord = fma(SMAA_AREATEX_PIXEL_SIZE, texcoord, 0.5 * SMAA_AREATEX_PIXEL_SIZE);

    // Move to proper place, according to the subpixel offset:
    texcoord.y = fma(SMAA_AREATEX_SUBTEX_SIZE, offset, texcoord.y);

    // Do it!
    return textureLod(sampler2D(areaTex, texSampler), texcoord,0.0).rg;
}

//-----------------------------------------------------------------------------
// Corner Detection Functions

void SMAADetectHorizontalCornerPattern(texture2D edgesTex, sampler texSampler, inout vec2 weights, vec4 texcoord, vec2 d) {
    #if !defined(SMAA_DISABLE_CORNER_DETECTION)
    vec2 leftRight = step(d.xy, d.yx);
    vec2 rounding = (1.0 - SMAA_CORNER_ROUNDING_NORM) * leftRight;

    rounding /= leftRight.x + leftRight.y; // Reduce blending for pixels in the center of a line.

    vec2 factor = vec2(1.0, 1.0);
    factor.x -= rounding.x * textureLodOffset(sampler2D(edgesTex, texSampler), texcoord.xy,0.0, ivec2(0,  1)).r;
    factor.x -= rounding.y * textureLodOffset(sampler2D(edgesTex, texSampler), texcoord.zw,0.0, ivec2(1,  1)).r;
    factor.y -= rounding.x * textureLodOffset(sampler2D(edgesTex, texSampler), texcoord.xy,0.0, ivec2(0, -2)).r;
    factor.y -= rounding.y * textureLodOffset(sampler2D(edgesTex, texSampler), texcoord.zw,0.0, ivec2(1, -2)).r;

    weights *= clamp(factor,0.0,1.0);
    #endif
}

void SMAADetectVerticalCornerPattern(texture2D edgesTex, sampler texSampler, inout vec2 weights, vec4 texcoord, vec2 d) {
    #if !defined(SMAA_DISABLE_CORNER_DETECTION)
    vec2 leftRight = step(d.xy, d.yx);
    vec2 rounding = (1.0 - SMAA_CORNER_ROUNDING_NORM) * leftRight;

    rounding /= leftRight.x + leftRight.y;

    vec2 factor = vec2(1.0, 1.0);
    factor.x -= rounding.x * textureLodOffset(sampler2D(edgesTex, texSampler), texcoord.xy, 0.0, ivec2( 1, 0)).g;
    factor.x -= rounding.y * textureLodOffset(sampler2D(edgesTex, texSampler), texcoord.zw, 0.0, ivec2( 1, 1)).g;
    factor.y -= rounding.x * textureLodOffset(sampler2D(edgesTex, texSampler), texcoord.xy, 0.0, ivec2(-2, 0)).g;
    factor.y -= rounding.y * textureLodOffset(sampler2D(edgesTex, texSampler), texcoord.zw, 0.0, ivec2(-2, 1)).g;

    weights *= clamp(factor,0.0,1.0);
    #endif
}

//-----------------------------------------------------------------------------
// Blending Weight Calculation Pixel Shader (Second Pass)

vec4 SMAABlendingWeightCalculationPS(vec2 texcoord,
                                       vec2 pixcoord,
                                       vec4 offset[3],
                                       vec4 rtInfo,
                                       texture2D edgesTex,
                                       texture2D areaTex,
                                       texture2D searchTex,
                                       sampler texSampler,
                                       vec4 subsampleIndices) { // Just pass zero for SMAA 1x, see @SUBSAMPLE_INDICES.
    vec4 weights = vec4(0.0, 0.0, 0.0, 0.0);

    vec2 e = texture(sampler2D(edgesTex,texSampler), texcoord).rg;

    SMAA_BRANCH
    if (e.g > 0.0) { // Edge at north
        #if !defined(SMAA_DISABLE_DIAG_DETECTION)
        // Diagonals have both north and west edges, so searching for them in
        // one of the boundaries is enough.
        weights.rg = SMAACalculateDiagWeights(edgesTex, areaTex, texSampler, texcoord, rtInfo, e, subsampleIndices);

        // We give priority to diagonals, so if we find a diagonal we skip 
        // horizontal/vertical processing.
        SMAA_BRANCH
        if (weights.r == -weights.g) { // weights.r + weights.g == 0.0
        #endif

        vec2 d;

        // Find the distance to the left:
        vec3 coords;
        coords.x = SMAASearchXLeft(edgesTex, searchTex, texSampler, offset[0].xy, rtInfo, offset[2].x);
        coords.y = offset[1].y; // offset[1].y = texcoord.y - 0.25 * rtInfo.y (@CROSSING_OFFSET)
        d.x = coords.x;

        // Now fetch the left crossing edges, two at a time using bilinear
        // filtering. Sampling at -0.25 (see @CROSSING_OFFSET) enables to
        // discern what value each edge has:
        float e1 = textureLod(sampler2D(edgesTex, texSampler), coords.xy,0.0).r;

        // Find the distance to the right:
        coords.z = SMAASearchXRight(edgesTex, searchTex, texSampler, offset[0].zw, rtInfo, offset[2].y);
        d.y = coords.z;

        // We want the distances to be in pixel units (doing this here allow to
        // better interleave arithmetic and memory accesses):
        d = abs(round(fma(rtInfo.zz, d, -pixcoord.xx)));

        // SMAAArea below needs a sqrt, as the areas texture is compressed
        // quadratically:
        vec2 sqrt_d = sqrt(d);

        // Fetch the right crossing edges:
        float e2 = textureLodOffset(sampler2D(edgesTex, texSampler), coords.zy,0.0, ivec2(1, 0)).r;

        // Ok, we know how this pattern looks like, now it is time for getting
        // the actual area:
        weights.rg = SMAAArea(areaTex, texSampler, sqrt_d, e1, e2, subsampleIndices.y);

        // Fix corners:
        coords.y = texcoord.y;
        SMAADetectHorizontalCornerPattern(edgesTex, texSampler, weights.rg, coords.xyzy, d);

        #if !defined(SMAA_DISABLE_DIAG_DETECTION)
        } else
            e.r = 0.0; // Skip vertical processing.
        #endif
    }

    SMAA_BRANCH
    if (e.r > 0.0) { // Edge at west
        vec2 d;

        // Find the distance to the top:
        vec3 coords;
        coords.y = SMAASearchYUp(edgesTex, searchTex, texSampler, offset[1].xy, rtInfo, offset[2].z);
        coords.x = offset[0].x; // offset[1].x = texcoord.x - 0.25 * rtInfo.x;
        d.x = coords.y;

        // Fetch the top crossing edges:
        float e1 = textureLod(sampler2D(edgesTex, texSampler), coords.xy,0.0).g;

        // Find the distance to the bottom:
        coords.z = SMAASearchYDown(edgesTex, searchTex, texSampler, offset[1].zw, rtInfo, offset[2].w);
        d.y = coords.z;

        // We want the distances to be in pixel units:
        d = abs(round(fma(rtInfo.ww, d, -pixcoord.yy)));

        // SMAAArea below needs a sqrt, as the areas texture is compressed 
        // quadratically:
        vec2 sqrt_d = sqrt(d);

        // Fetch the bottom crossing edges:
        float e2 = textureLodOffset(sampler2D(edgesTex, texSampler), coords.xz, 0.0,ivec2(0, 1)).g;

        // Get the area for this direction:
        weights.ba = SMAAArea(areaTex, texSampler, sqrt_d, e1, e2, subsampleIndices.x);

        // Fix corners:
        coords.x = texcoord.x;
        SMAADetectVerticalCornerPattern(edgesTex, texSampler, weights.ba, coords.xyxz, d);
    }

    return weights;
}

void main()
{
    outFragColour = SMAABlendingWeightCalculationPS(
        vTexCoord0,
        vPixCoord0,
        vOffset,
        texelSize.value,
        uEdgeTexture,
        uAreaTexture,
        uSearchTexture,
        uSampler,
        vec4(0));

}