namespace TradingFlow.Research.Statistics;

public sealed record BlockBootstrapInterval(
    double Estimate,
    double LowerBound,
    double UpperBound,
    double ConfidenceLevel,
    int BlockLength,
    int Replicates,
    int Seed,
    string Method);

public sealed record HacMeanInference(
    double Estimate,
    double StandardError,
    double TestStatistic,
    double TwoSidedPValue,
    int Lag,
    int ObservationCount,
    string Method);

public sealed record AdjustedHypothesisPValue(
    string HypothesisId,
    double RawPValue,
    double AdjustedPValue,
    bool Rejected);

public sealed record SelectionBiasAuditMetric(
    double ObservedSharpe,
    double ExpectedMaximumSharpe,
    double SharpeStandardError,
    double DeflatedSharpeProbability,
    int IndependentTrialCount,
    int ObservationCount,
    string Method,
    string Limitation);

public static class ResearchInference
{
    public static BlockBootstrapInterval BlockBootstrapMean(
        IReadOnlyList<double> observations,
        int blockLength,
        int replicates,
        double confidenceLevel,
        int seed)
    {
        var values = RequireFiniteObservations(observations, minimumCount: 2);
        if (blockLength < 1 || blockLength > values.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(blockLength));
        }

        if (replicates < 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(replicates),
                "At least 100 bootstrap replicates are required.");
        }

        if (confidenceLevel <= 0 || confidenceLevel >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(confidenceLevel));
        }

        var random = new Random(seed);
        var means = new double[replicates];
        var sample = new double[values.Length];
        for (var replicate = 0; replicate < replicates; replicate++)
        {
            var written = 0;
            while (written < sample.Length)
            {
                var start = random.Next(values.Length);
                for (var offset = 0;
                     offset < blockLength && written < sample.Length;
                     offset++)
                {
                    sample[written++] = values[(start + offset) % values.Length];
                }
            }

            means[replicate] = sample.Average();
        }

        Array.Sort(means);
        var tail = (1 - confidenceLevel) / 2;
        return new BlockBootstrapInterval(
            values.Average(),
            Quantile(means, tail),
            Quantile(means, 1 - tail),
            confidenceLevel,
            blockLength,
            replicates,
            seed,
            "circular-moving-block-bootstrap-mean");
    }

    public static HacMeanInference NeweyWestMean(
        IReadOnlyList<double> observations,
        int lag)
    {
        var values = RequireFiniteObservations(observations, minimumCount: 2);
        if (lag < 0 || lag >= values.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(lag));
        }

        var mean = values.Average();
        var centered = values.Select(value => value - mean).ToArray();
        var longRunVariance = centered.Sum(value => value * value) / values.Length;
        for (var currentLag = 1; currentLag <= lag; currentLag++)
        {
            var covariance = 0.0;
            for (var index = currentLag; index < centered.Length; index++)
            {
                covariance += centered[index] * centered[index - currentLag];
            }

            covariance /= values.Length;
            var bartlettWeight = 1.0 - currentLag / (double)(lag + 1);
            longRunVariance += 2.0 * bartlettWeight * covariance;
        }

        longRunVariance = Math.Max(0, longRunVariance);
        var standardError = Math.Sqrt(longRunVariance / values.Length);
        var statistic = standardError > 0
            ? mean / standardError
            : mean == 0
                ? 0
                : Math.CopySign(Double.PositiveInfinity, mean);
        var pValue = Double.IsInfinity(statistic)
            ? 0
            : 2 * (1 - NormalCdf(Math.Abs(statistic)));
        return new HacMeanInference(
            mean,
            standardError,
            statistic,
            Math.Clamp(pValue, 0, 1),
            lag,
            values.Length,
            "newey-west-bartlett-mean-normal-approximation");
    }

    public static IReadOnlyList<AdjustedHypothesisPValue> HolmAdjust(
        IReadOnlyDictionary<string, double> pValues,
        double familyWiseAlpha)
    {
        ArgumentNullException.ThrowIfNull(pValues);
        if (pValues.Count == 0)
        {
            throw new ArgumentException("At least one hypothesis is required.", nameof(pValues));
        }

        if (familyWiseAlpha <= 0 || familyWiseAlpha >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(familyWiseAlpha));
        }

        var ordered = pValues
            .Select(item =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(item.Key);
                if (!Double.IsFinite(item.Value) || item.Value < 0 || item.Value > 1)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(pValues),
                        "P-values must be finite values between zero and one.");
                }

                return item;
            })
            .OrderBy(item => item.Value)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .ToArray();

        var adjusted = new Dictionary<string, AdjustedHypothesisPValue>(StringComparer.Ordinal);
        var runningMaximum = 0.0;
        for (var index = 0; index < ordered.Length; index++)
        {
            var multiplier = ordered.Length - index;
            runningMaximum = Math.Max(
                runningMaximum,
                Math.Min(1, ordered[index].Value * multiplier));
            adjusted[ordered[index].Key] = new AdjustedHypothesisPValue(
                ordered[index].Key,
                ordered[index].Value,
                runningMaximum,
                runningMaximum <= familyWiseAlpha);
        }

        return adjusted.Values
            .OrderBy(item => item.HypothesisId, StringComparer.Ordinal)
            .ToArray();
    }

    public static SelectionBiasAuditMetric DeflatedSharpeCompatibleAudit(
        double observedSharpe,
        int observationCount,
        int independentTrialCount,
        double skewness,
        double kurtosis)
    {
        if (!Double.IsFinite(observedSharpe) ||
            !Double.IsFinite(skewness) ||
            !Double.IsFinite(kurtosis))
        {
            throw new ArgumentOutOfRangeException(
                nameof(observedSharpe),
                "Audit inputs must be finite.");
        }

        if (observationCount < 3)
        {
            throw new ArgumentOutOfRangeException(nameof(observationCount));
        }

        if (independentTrialCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(independentTrialCount));
        }

        if (kurtosis < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(kurtosis),
                "Kurtosis must be Pearson kurtosis and cannot be below one.");
        }

        var sharpeVarianceNumerator =
            1 - skewness * observedSharpe +
            ((kurtosis - 1) / 4.0) * observedSharpe * observedSharpe;
        var sharpeStandardError = Math.Sqrt(
            Math.Max(0, sharpeVarianceNumerator) / (observationCount - 1));
        var expectedMaximum = independentTrialCount == 1
            ? 0
            : sharpeStandardError *
              ((1 - 0.5772156649015329) *
               InverseNormalCdf(1 - 1.0 / independentTrialCount) +
               0.5772156649015329 *
               InverseNormalCdf(1 - 1.0 / (independentTrialCount * Math.E)));
        var probability = sharpeStandardError > 0
            ? NormalCdf((observedSharpe - expectedMaximum) / sharpeStandardError)
            : observedSharpe > expectedMaximum
                ? 1
                : 0;

        return new SelectionBiasAuditMetric(
            observedSharpe,
            expectedMaximum,
            sharpeStandardError,
            Math.Clamp(probability, 0, 1),
            independentTrialCount,
            observationCount,
            "deflated-sharpe-compatible-normal-approximation",
            "Audit diagnostic only: the probability depends on the supplied effective " +
            "independent-trial count, sample moments, and an asymptotic normal approximation; " +
            "it is not independent proof of alpha or a promotion decision.");
    }

    private static double[] RequireFiniteObservations(
        IReadOnlyList<double> observations,
        int minimumCount)
    {
        ArgumentNullException.ThrowIfNull(observations);
        if (observations.Count < minimumCount)
        {
            throw new ArgumentException(
                $"At least {minimumCount} observations are required.",
                nameof(observations));
        }

        var values = observations.ToArray();
        if (values.Any(value => !Double.IsFinite(value)))
        {
            throw new ArgumentException("Observations must be finite.", nameof(observations));
        }

        return values;
    }

    private static double Quantile(IReadOnlyList<double> sortedValues, double probability)
    {
        var position = probability * (sortedValues.Count - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper)
        {
            return sortedValues[lower];
        }

        var weight = position - lower;
        return sortedValues[lower] * (1 - weight) + sortedValues[upper] * weight;
    }

    private static double NormalCdf(double value)
    {
        var absolute = Math.Abs(value);
        var t = 1.0 / (1.0 + 0.2316419 * absolute);
        var density = 0.3989422804014327 * Math.Exp(-0.5 * absolute * absolute);
        var probability = 1 - density * t *
            (0.319381530 + t *
             (-0.356563782 + t *
              (1.781477937 + t *
               (-1.821255978 + t * 1.330274429))));
        return value >= 0 ? probability : 1 - probability;
    }

    // Peter J. Acklam's rational approximation, sufficient for audit thresholds.
    private static double InverseNormalCdf(double probability)
    {
        if (probability <= 0 || probability >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(probability));
        }

        double[] a =
        [
            -3.969683028665376e+01, 2.209460984245205e+02,
            -2.759285104469687e+02, 1.383577518672690e+02,
            -3.066479806614716e+01, 2.506628277459239e+00
        ];
        double[] b =
        [
            -5.447609879822406e+01, 1.615858368580409e+02,
            -1.556989798598866e+02, 6.680131188771972e+01,
            -1.328068155288572e+01
        ];
        double[] c =
        [
            -7.784894002430293e-03, -3.223964580411365e-01,
            -2.400758277161838e+00, -2.549732539343734e+00,
            4.374664141464968e+00, 2.938163982698783e+00
        ];
        double[] d =
        [
            7.784695709041462e-03, 3.224671290700398e-01,
            2.445134137142996e+00, 3.754408661907416e+00
        ];
        const double lower = 0.02425;
        const double upper = 1 - lower;
        if (probability < lower)
        {
            var q = Math.Sqrt(-2 * Math.Log(probability));
            return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) /
                   ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }

        if (probability <= upper)
        {
            var q = probability - 0.5;
            var r = q * q;
            return (((((a[0] * r + a[1]) * r + a[2]) * r + a[3]) * r + a[4]) * r + a[5]) * q /
                   (((((b[0] * r + b[1]) * r + b[2]) * r + b[3]) * r + b[4]) * r + 1);
        }

        var upperQ = Math.Sqrt(-2 * Math.Log(1 - probability));
        return -(((((c[0] * upperQ + c[1]) * upperQ + c[2]) * upperQ + c[3]) * upperQ + c[4]) * upperQ + c[5]) /
               ((((d[0] * upperQ + d[1]) * upperQ + d[2]) * upperQ + d[3]) * upperQ + 1);
    }
}
