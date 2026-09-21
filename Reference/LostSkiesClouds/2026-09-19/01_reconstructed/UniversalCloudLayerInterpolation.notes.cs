// STRUCTURAL RECONSTRUCTION -- NOT A DROP-IN IMPLEMENTATION OR ORIGINAL SOURCE.
// This file records what UniversalCloudLayer.lerp provably does without inventing
// names for every inlined native operation. See the Korean reconstruction notes.

namespace Expanse;

internal static class UniversalCloudLayerInterpolationNotes
{
    /*
    UniversalCloudLayer lerp(a, b, x, c):

      1. Reuse c as the output object.
      2. Choose discrete fields from a while x < 0.5, otherwise from b.
      3. Linearly interpolate continuous renderSettings fields.
      4. For each of six noiseLayers:
           - copy the selected side's UniversalCloudNoiseLayer structure;
           - interpolate the integer tile field from a to b.
      5. Interpolate densityCurve and coverageCurve element by element.
      6. Rebuild densityAnimationCurve and coverageAnimationCurve by calling
         Utilities.curveFromSamples on those arrays.
      7. Select customPostProcessPasses from a or b at the 0.5 threshold.
      8. Return c.

    UniversalCloudNoiseLayer lerp(a, b, x):

      The observed body copies a and returns it. It does not read b or x.
      Texture blending is performed by CloudLayerInterpolator instead.
    */
}
