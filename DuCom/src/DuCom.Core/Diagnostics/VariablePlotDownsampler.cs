namespace DuCom.Core.Diagnostics;

public static class VariablePlotDownsampler
{
    public static IReadOnlyList<VariableNumericSample> MinMax(
        IReadOnlyList<VariableNumericSample> samples,
        int maximumOutputPoints)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumOutputPoints);
        if (samples.Count <= maximumOutputPoints)
        {
            return Array.AsReadOnly(samples.ToArray());
        }

        if (maximumOutputPoints == 1)
        {
            return Array.AsReadOnly([samples[0]]);
        }

        List<VariableNumericSample> result = new(maximumOutputPoints) { samples[0] };
        int interiorBudget = maximumOutputPoints - 2;
        int interiorCount = samples.Count - 2;
        int bucketCount = Math.Max(1, (interiorBudget + 1) / 2);
        for (int bucket = 0; bucket < bucketCount && result.Count < maximumOutputPoints - 1; bucket++)
        {
            int start = 1 + bucket * interiorCount / bucketCount;
            int end = 1 + (bucket + 1) * interiorCount / bucketCount;
            if (start >= end)
            {
                continue;
            }

            int minIndex = start;
            int maxIndex = start;
            for (int index = start + 1; index < end; index++)
            {
                if (samples[index].NumericValue < samples[minIndex].NumericValue) minIndex = index;
                if (samples[index].NumericValue > samples[maxIndex].NumericValue) maxIndex = index;
            }

            if (minIndex == maxIndex || result.Count == maximumOutputPoints - 2)
            {
                result.Add(samples[Math.Abs(samples[minIndex].NumericValue) >= Math.Abs(samples[maxIndex].NumericValue) ? minIndex : maxIndex]);
            }
            else
            {
                result.Add(samples[Math.Min(minIndex, maxIndex)]);
                if (result.Count < maximumOutputPoints - 1)
                {
                    result.Add(samples[Math.Max(minIndex, maxIndex)]);
                }
            }
        }

        result.Add(samples[^1]);
        return Array.AsReadOnly(result.ToArray());
    }
}
