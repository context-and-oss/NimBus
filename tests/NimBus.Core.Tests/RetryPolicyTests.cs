#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using NimBus.Core.Messages;

namespace NimBus.Core.Tests;

[TestClass]
public class RetryPolicyTests
{
    [TestMethod]
    public void GetDelay_DefaultJitter_PreservesExistingBackoffValues()
    {
        var fixedPolicy = new RetryPolicy
        {
            Strategy = BackoffStrategy.Fixed,
            BaseDelay = TimeSpan.FromSeconds(5),
        };
        var linearPolicy = new RetryPolicy
        {
            Strategy = BackoffStrategy.Linear,
            BaseDelay = TimeSpan.FromSeconds(5),
        };
        var exponentialPolicy = new RetryPolicy
        {
            Strategy = BackoffStrategy.Exponential,
            BaseDelay = TimeSpan.FromSeconds(5),
        };
        var cappedPolicy = new RetryPolicy
        {
            Strategy = BackoffStrategy.Exponential,
            BaseDelay = TimeSpan.FromSeconds(5),
            MaxDelay = TimeSpan.FromMinutes(1),
        };

        Assert.AreEqual(JitterMode.None, fixedPolicy.Jitter);
        Assert.AreEqual(0.25, fixedPolicy.BoundedJitterFactor, 0.000001);
        var random = new FixedRandom(0.5);
        Assert.AreEqual(TimeSpan.FromSeconds(5), fixedPolicy.GetDelay(3, random));
        Assert.AreEqual(0, random.NextDoubleCalls);
        Assert.AreEqual(TimeSpan.FromSeconds(15), linearPolicy.GetDelay(2));
        Assert.AreEqual(TimeSpan.FromSeconds(40), exponentialPolicy.GetDelay(3));
        Assert.AreEqual(TimeSpan.FromMinutes(1), cappedPolicy.GetDelay(6));
        Assert.AreEqual(2, new RetryPolicy { BaseDelay = TimeSpan.FromSeconds(90) }.GetDelayMinutes(0));
    }

    [TestMethod]
    public void GetDelay_FullJitter_StaysWithinExpectedRange()
    {
        var baseDelay = TimeSpan.FromSeconds(10);
        var policy = new RetryPolicy
        {
            Strategy = BackoffStrategy.Fixed,
            BaseDelay = baseDelay,
            Jitter = JitterMode.Full,
        };
        var random = new Random(12345);

        Assert.AreEqual(TimeSpan.FromSeconds(15), policy.GetDelay(0, new FixedRandom(0.5)));
        Assert.IsTrue(policy.GetDelay(0, new FixedRandom(Math.BitDecrement(1.0))) < baseDelay * 2);

        for (var sample = 0; sample < 100; sample++)
        {
            var delay = policy.GetDelay(0, random);

            Assert.IsTrue(delay >= baseDelay, $"Sample {sample} was below the base delay: {delay}.");
            Assert.IsTrue(delay < baseDelay * 2, $"Sample {sample} reached or exceeded twice the base delay: {delay}.");
        }
    }

    [TestMethod]
    public void GetDelay_BoundedJitter_StaysWithinConfiguredRange()
    {
        var baseDelay = TimeSpan.FromSeconds(10);
        const double factor = 0.4;
        var policy = new RetryPolicy
        {
            Strategy = BackoffStrategy.Fixed,
            BaseDelay = baseDelay,
            Jitter = JitterMode.Bounded,
            BoundedJitterFactor = factor,
        };
        var random = new Random(54321);
        var upperBound = baseDelay * (1 + factor);

        Assert.AreEqual(TimeSpan.FromSeconds(12), policy.GetDelay(0, new FixedRandom(0.5)));
        Assert.IsTrue(policy.GetDelay(0, new FixedRandom(Math.BitDecrement(1.0))) < upperBound);

        for (var sample = 0; sample < 100; sample++)
        {
            var delay = policy.GetDelay(0, random);

            Assert.IsTrue(delay >= baseDelay, $"Sample {sample} was below the base delay: {delay}.");
            Assert.IsTrue(delay < upperBound, $"Sample {sample} reached or exceeded the bounded jitter range: {delay}.");
        }
    }

    [TestMethod]
    public void GetDelay_MaxDelayCapsAfterJitter()
    {
        var baseDelay = TimeSpan.FromSeconds(10);
        var maxDelay = TimeSpan.FromSeconds(12);
        var uncappedPolicy = new RetryPolicy
        {
            BaseDelay = baseDelay,
            Jitter = JitterMode.Full,
        };
        var cappedPolicy = new RetryPolicy
        {
            BaseDelay = baseDelay,
            Jitter = JitterMode.Full,
            MaxDelay = maxDelay,
        };
        var boundedCappedPolicy = new RetryPolicy
        {
            BaseDelay = baseDelay,
            Jitter = JitterMode.Bounded,
            BoundedJitterFactor = 0.5,
            MaxDelay = maxDelay,
        };

        Assert.AreEqual(TimeSpan.FromSeconds(15), uncappedPolicy.GetDelay(0, new FixedRandom(0.5)));
        Assert.AreEqual(maxDelay, cappedPolicy.GetDelay(0, new FixedRandom(0.5)));
        Assert.AreEqual(maxDelay, boundedCappedPolicy.GetDelay(0, new FixedRandom(0.5)));
    }

    [TestMethod]
    public void GetDelay_MaxDelayCapsJitterThatWouldOverflowTimeSpan()
    {
        var maxDelay = TimeSpan.FromMinutes(5);
        var fullPolicy = new RetryPolicy
        {
            BaseDelay = TimeSpan.FromTicks(long.MaxValue - 1_000),
            Jitter = JitterMode.Full,
            MaxDelay = maxDelay,
        };
        var boundedPolicy = new RetryPolicy
        {
            BaseDelay = TimeSpan.FromSeconds(10),
            Jitter = JitterMode.Bounded,
            BoundedJitterFactor = double.MaxValue,
            MaxDelay = maxDelay,
        };

        Assert.AreEqual(maxDelay, fullPolicy.GetDelay(0, new FixedRandom(0.5)));
        Assert.AreEqual(maxDelay, boundedPolicy.GetDelay(0, new FixedRandom(0.5)));
    }

    [TestMethod]
    [DataRow(-0.01)]
    [DataRow(double.NaN)]
    [DataRow(double.PositiveInfinity)]
    public void GetDelay_BoundedJitter_RejectsInvalidFactor(double factor)
    {
        var policy = new RetryPolicy
        {
            BaseDelay = TimeSpan.FromSeconds(10),
            Jitter = JitterMode.Bounded,
            BoundedJitterFactor = factor,
        };

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => policy.GetDelay(0, new FixedRandom(0.5)));
    }

    [TestMethod]
    [DataRow(12)]
    [DataRow(40)]
    [DataRow(62)]
    [DataRow(63)]
    [DataRow(64)]
    [DataRow(1000)]
    [DataRow(int.MaxValue)]
    public void GetDelay_ExponentialSmallBase_SaturatesAtMaxDelay(int retryAttempt)
    {
        var maxDelay = TimeSpan.FromMinutes(5);
        var policy = new RetryPolicy
        {
            Strategy = BackoffStrategy.Exponential,
            BaseDelay = TimeSpan.FromSeconds(1),
            MaxDelay = maxDelay,
        };

        var delay = policy.GetDelay(retryAttempt);

        Assert.AreEqual(maxDelay, delay);
        Assert.IsTrue(delay > TimeSpan.Zero);
    }

    [TestMethod]
    [DataRow(1L, 60L, 6)]
    [DataRow(1L, 60L, 34)]
    [DataRow(1L, 60L, 63)]
    [DataRow(1L, 60L, 1000)]
    [DataRow(1L, 60L, int.MaxValue)]
    [DataRow(1440L, 43200L, 5)]
    [DataRow(1440L, 43200L, 100)]
    [DataRow(1440L, 43200L, int.MaxValue)]
    public void GetDelay_ExponentialLargeBase_SaturatesAtMaxDelay(long baseMinutes, long maxMinutes, int retryAttempt)
    {
        var maxDelay = TimeSpan.FromMinutes(maxMinutes);
        var policy = new RetryPolicy
        {
            Strategy = BackoffStrategy.Exponential,
            BaseDelay = TimeSpan.FromMinutes(baseMinutes),
            MaxDelay = maxDelay,
        };

        var delay = policy.GetDelay(retryAttempt);

        Assert.AreEqual(maxDelay, delay);
        Assert.IsTrue(delay > TimeSpan.Zero);
    }

    [TestMethod]
    [DataRow(JitterMode.Full, false)]
    [DataRow(JitterMode.Full, true)]
    [DataRow(JitterMode.Bounded, false)]
    [DataRow(JitterMode.Bounded, true)]
    public void GetDelay_ExponentialWithJitterAtSaturation_ReturnsMaxDelay(JitterMode jitter, bool largestRandom)
    {
        var maxDelay = TimeSpan.FromMinutes(5);
        var policy = new RetryPolicy
        {
            Strategy = BackoffStrategy.Exponential,
            BaseDelay = TimeSpan.FromSeconds(1),
            MaxDelay = maxDelay,
            Jitter = jitter,
            BoundedJitterFactor = 0.25,
        };
        Random random = largestRandom ? new FixedRandom(Math.BitDecrement(1.0)) : new Random(4242);

        Assert.AreEqual(maxDelay, policy.GetDelay(int.MaxValue, random));
    }

    [TestMethod]
    [DataRow(BackoffStrategy.Fixed, 5L, null, 0, 5L)]
    [DataRow(BackoffStrategy.Fixed, 5L, null, 10, 5L)]
    [DataRow(BackoffStrategy.Linear, 5L, null, 0, 5L)]
    [DataRow(BackoffStrategy.Linear, 5L, null, 3, 20L)]
    [DataRow(BackoffStrategy.Exponential, 5L, null, 0, 5L)]
    [DataRow(BackoffStrategy.Exponential, 5L, null, 1, 10L)]
    [DataRow(BackoffStrategy.Exponential, 5L, null, 6, 320L)]
    [DataRow(BackoffStrategy.Exponential, 1L, null, 30, 1L << 30)]
    [DataRow(BackoffStrategy.Exponential, 1L, 300L, 8, 256L)]
    [DataRow(BackoffStrategy.Exponential, 600L, 300L, 0, 300L)]
    public void GetDelay_NonOverflowingInputs_KeepExistingValues(
        BackoffStrategy strategy, long baseSeconds, long? maxSeconds, int retryAttempt, long expectedSeconds)
    {
        var policy = new RetryPolicy
        {
            Strategy = strategy,
            BaseDelay = TimeSpan.FromSeconds(baseSeconds),
            MaxDelay = maxSeconds.HasValue ? TimeSpan.FromSeconds(maxSeconds.Value) : null,
        };

        Assert.AreEqual(TimeSpan.FromSeconds(expectedSeconds), policy.GetDelay(retryAttempt));
    }

    [TestMethod]
    [DataRow(JitterMode.Full)]
    [DataRow(JitterMode.Bounded)]
    public void GetDelay_ExponentialSeededJitter_MatchesPreviousFormula(JitterMode jitter)
    {
        const int seed = 2024;
        var baseDelay = TimeSpan.FromSeconds(5);
        var maxDelay = TimeSpan.FromMinutes(1);
        var policy = new RetryPolicy
        {
            Strategy = BackoffStrategy.Exponential,
            BaseDelay = baseDelay,
            MaxDelay = maxDelay,
            Jitter = jitter,
        };
        var factor = jitter == JitterMode.Full ? 1 : policy.BoundedJitterFactor;
        var actualRandom = new Random(seed);
        var expectedRandom = new Random(seed);

        for (var attempt = 0; attempt <= 5; attempt++)
        {
            // The pre-change formula, computed independently.
            var delay = TimeSpan.FromTicks(baseDelay.Ticks * (long)Math.Pow(2, attempt));
            var maximumJitterTicks = delay.Ticks * factor;
            var jitterTicks = (long)(maximumJitterTicks * expectedRandom.NextDouble());
            if (maximumJitterTicks > 0 && jitterTicks >= maximumJitterTicks)
                jitterTicks = (long)Math.Ceiling(maximumJitterTicks) - 1;
            var expected = (decimal)delay.Ticks + jitterTicks > maxDelay.Ticks
                ? maxDelay
                : TimeSpan.FromTicks(delay.Ticks + jitterTicks);

            Assert.AreEqual(expected, policy.GetDelay(attempt, actualRandom), $"Attempt {attempt}.");
        }
    }

    [TestMethod]
    [DataRow(BackoffStrategy.Fixed, false, 0)]
    [DataRow(BackoffStrategy.Fixed, false, 63)]
    [DataRow(BackoffStrategy.Fixed, false, int.MaxValue)]
    [DataRow(BackoffStrategy.Fixed, true, 0)]
    [DataRow(BackoffStrategy.Fixed, true, 63)]
    [DataRow(BackoffStrategy.Fixed, true, int.MaxValue)]
    [DataRow(BackoffStrategy.Linear, false, 0)]
    [DataRow(BackoffStrategy.Linear, false, 63)]
    [DataRow(BackoffStrategy.Linear, false, int.MaxValue)]
    [DataRow(BackoffStrategy.Linear, true, 0)]
    [DataRow(BackoffStrategy.Linear, true, 63)]
    [DataRow(BackoffStrategy.Linear, true, int.MaxValue)]
    [DataRow(BackoffStrategy.Exponential, false, 0)]
    [DataRow(BackoffStrategy.Exponential, false, 63)]
    [DataRow(BackoffStrategy.Exponential, false, int.MaxValue)]
    [DataRow(BackoffStrategy.Exponential, true, 0)]
    [DataRow(BackoffStrategy.Exponential, true, 63)]
    [DataRow(BackoffStrategy.Exponential, true, int.MaxValue)]
    public void GetDelay_ZeroBaseDelay_ReturnsZero(BackoffStrategy strategy, bool withMaxDelay, int retryAttempt)
    {
        var policy = new RetryPolicy
        {
            Strategy = strategy,
            BaseDelay = TimeSpan.Zero,
            MaxDelay = withMaxDelay ? TimeSpan.FromMinutes(5) : null,
        };

        Assert.AreEqual(TimeSpan.Zero, policy.GetDelay(retryAttempt));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(63)]
    [DataRow(int.MaxValue)]
    public void GetDelay_ExponentialZeroMaxDelay_ReturnsZero(int retryAttempt)
    {
        var policy = new RetryPolicy
        {
            Strategy = BackoffStrategy.Exponential,
            BaseDelay = TimeSpan.FromMinutes(1),
            MaxDelay = TimeSpan.Zero,
        };

        Assert.AreEqual(TimeSpan.Zero, policy.GetDelay(retryAttempt));
        Assert.AreEqual(0, policy.GetDelayMinutes(retryAttempt));
    }

    [TestMethod]
    [DataRow(0, 1)]
    [DataRow(1, 2)]
    [DataRow(2, 4)]
    [DataRow(3, 5)]
    [DataRow(34, 5)]
    [DataRow(1000, 5)]
    [DataRow(int.MaxValue, 5)]
    public void GetDelayMinutes_ExponentialWithMaxDelay_SaturatesAtCap(int retryAttempt, int expectedMinutes)
    {
        var policy = new RetryPolicy
        {
            Strategy = BackoffStrategy.Exponential,
            BaseDelay = TimeSpan.FromMinutes(1),
            MaxDelay = TimeSpan.FromMinutes(5),
        };

        Assert.AreEqual(expectedMinutes, policy.GetDelayMinutes(retryAttempt));
    }

    [TestMethod]
    public void NewtonsoftSerialization_PreservesLegacyDefaultsAndJitterSettings()
    {
        const string legacyJson = "{\"MaxRetries\":3,\"Strategy\":2,\"BaseDelay\":\"00:00:05\",\"MaxDelay\":\"00:01:00\"}";

        var legacyPolicy = JsonConvert.DeserializeObject<RetryPolicy>(legacyJson);

        Assert.IsNotNull(legacyPolicy);
        Assert.AreEqual(3, legacyPolicy.MaxRetries);
        Assert.AreEqual(BackoffStrategy.Exponential, legacyPolicy.Strategy);
        Assert.AreEqual(TimeSpan.FromSeconds(5), legacyPolicy.BaseDelay);
        Assert.AreEqual(TimeSpan.FromMinutes(1), legacyPolicy.MaxDelay);
        Assert.AreEqual(JitterMode.None, legacyPolicy.Jitter);
        Assert.AreEqual(0.25, legacyPolicy.BoundedJitterFactor, 0.000001);

        var configuredPolicy = new RetryPolicy
        {
            Jitter = JitterMode.Bounded,
            BoundedJitterFactor = 0.4,
        };
        var roundTrippedPolicy = JsonConvert.DeserializeObject<RetryPolicy>(
            JsonConvert.SerializeObject(configuredPolicy));

        Assert.IsNotNull(roundTrippedPolicy);
        Assert.AreEqual(JitterMode.Bounded, roundTrippedPolicy.Jitter);
        Assert.AreEqual(0.4, roundTrippedPolicy.BoundedJitterFactor, 0.000001);
    }

    private sealed class FixedRandom : Random
    {
        private readonly double _value;

        public FixedRandom(double value)
        {
            _value = value;
        }

        public int NextDoubleCalls { get; private set; }

        public override double NextDouble()
        {
            NextDoubleCalls++;
            return _value;
        }
    }
}
