using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Midora.AudioDevice.BassWasapi.Tests.Console;

/// <summary>
/// 一个有限长度、线程安全的正弦波离散采样生成器。
/// </summary>
public sealed class SineWave
{
    private const double Tau = double.Tau;

    // ulong 最大值 + 1。用于在 double 范围内做上界检查。
    private const double ULongExclusiveUpperBound =
        18_446_744_073_709_551_616.0;

    private readonly object _gate = new();

    private readonly double _phaseIncrement;
    private readonly ulong _totalSampleCount;

    private readonly TaskCompletionSource<bool> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private double _phase;
    private ulong _generatedSampleCount;

    /// <param name="frequencyHz">正弦波频率，单位 Hz，必须大于等于 0。</param>
    /// <param name="sampleRate">采样率，单位 Hz，必须大于 0。</param>
    /// <param name="durationSeconds">持续时间，单位秒，必须大于等于 0。</param>
    public SineWave(
        double frequencyHz,
        uint sampleRate,
        double durationSeconds,
        double a = 1d,
        bool useCos = false)
    {
        if (!double.IsFinite(frequencyHz) || frequencyHz < 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frequencyHz),
                frequencyHz,
                "频率必须是大于等于 0 的有限数值。");
        }

        if (sampleRate == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleRate),
                sampleRate,
                "采样率必须大于 0。");
        }

        if (!double.IsFinite(durationSeconds) || durationSeconds < 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(durationSeconds),
                durationSeconds,
                "持续时间必须是大于等于 0 的有限数值。");
        }

        FrequencyHz = frequencyHz;
        SampleRate = sampleRate;
        DurationSeconds = durationSeconds;
        A = a;
        UseCos = useCos;

        double exactSampleCount = durationSeconds * sampleRate;

        if (!double.IsFinite(exactSampleCount) ||
            exactSampleCount >= ULongExclusiveUpperBound)
        {
            throw new ArgumentOutOfRangeException(
                nameof(durationSeconds),
                durationSeconds,
                "持续时间与采样率的乘积过大。");
        }

        // 取最接近的整数采样数量。
        _totalSampleCount = checked(
            (ulong)Math.Round(
                exactSampleCount,
                MidpointRounding.AwayFromZero));

        /*
         * 对频率先按采样率取模。
         *
         * 对离散采样而言，f 和 f + k * sampleRate 会产生相同的采样序列。
         * 这样还能避免 Tau * frequencyHz 对极大频率产生溢出。
         */
        double discreteFrequency = frequencyHz % sampleRate;

        _phaseIncrement =
            (Tau * discreteFrequency / sampleRate) % Tau;

        if (_totalSampleCount == 0)
        {
            _completion.SetResult(true);
        }
    }

    public double FrequencyHz { get; }

    public uint SampleRate { get; }

    public double DurationSeconds { get; }

    public double A { get; }

    public bool UseCos { get; }

    public ulong TotalSampleCount => _totalSampleCount;

    /// <summary>
    /// 经过整数采样数量修正后的实际持续时间。
    /// </summary>
    public double ActualDurationSeconds =>
        (double)_totalSampleCount / SampleRate;

    public bool IsCompleted => _completion.Task.IsCompleted;

    public ulong GeneratedSampleCount
    {
        get
        {
            lock (_gate)
            {
                return _generatedSampleCount;
            }
        }
    }

    public ulong RemainingSampleCount
    {
        get
        {
            lock (_gate)
            {
                return _totalSampleCount - _generatedSampleCount;
            }
        }
    }

    /// <summary>
    /// 将最多 sampleCount 个采样写入 dest，并推进生成器状态。
    /// </summary>
    /// <returns>
    /// 实际写入的采样数量。
    /// 返回 0 表示生成器已经到达末尾，或者 sampleCount 本身为 0。
    /// </returns>
    public unsafe uint Take(float* dest, uint sampleCount)
    {
        // 允许 Take(null, 0)。
        if (sampleCount == 0)
        {
            return 0;
        }

        if (dest == null)
        {
            throw new ArgumentNullException(nameof(dest));
        }

        uint written;
        bool reachedEnd;

        lock (_gate)
        {
            ulong remaining =
                _totalSampleCount - _generatedSampleCount;

            if (remaining == 0)
            {
                return 0;
            }

            written = (uint)Math.Min(
                (ulong)sampleCount,
                remaining);

            double phase = _phase;
            double phaseIncrement = _phaseIncrement;
            float* output = dest;

            for (uint i = 0; i < written; i++)
            {
                *output++ = (float)(A * (UseCos ? Math.Cos(phase) : Math.Sin(phase)));

                phase += phaseIncrement;

                // phaseIncrement 已经处于 [0, Tau)，所以最多减一次。
                if (phase >= Tau)
                {
                    phase -= Tau;
                }
            }

            _phase = phase;
            _generatedSampleCount += written;

            reachedEnd =
                _generatedSampleCount == _totalSampleCount;
        }

        /*
         * 在锁外发出完成信号。
         * TrySetResult 也让这里能安全应对未来代码修改带来的重复通知。
         */
        if (reachedEnd)
        {
            _completion.TrySetResult(true);
        }

        return written;
    }

    public unsafe uint TakeAdd(float* dest, uint sampleCount)
    {
        // 允许 Take(null, 0)。
        if (sampleCount == 0)
        {
            return 0;
        }

        if (dest == null)
        {
            throw new ArgumentNullException(nameof(dest));
        }

        uint written;
        bool reachedEnd;

        lock (_gate)
        {
            ulong remaining =
                _totalSampleCount - _generatedSampleCount;

            if (remaining == 0)
            {
                return 0;
            }

            written = (uint)Math.Min(
                (ulong)sampleCount,
                remaining);

            double phase = _phase;
            double phaseIncrement = _phaseIncrement;
            float* output = dest;

            for (uint i = 0; i < written; i++)
            {
                *output = *output + (float)(A * (UseCos ? Math.Cos(phase) : Math.Sin(phase)));
                output++;

                phase += phaseIncrement;

                // phaseIncrement 已经处于 [0, Tau)，所以最多减一次。
                if (phase >= Tau)
                {
                    phase -= Tau;
                }
            }

            _phase = phase;
            _generatedSampleCount += written;

            reachedEnd =
                _generatedSampleCount == _totalSampleCount;
        }

        /*
         * 在锁外发出完成信号。
         * TrySetResult 也让这里能安全应对未来代码修改带来的重复通知。
         */
        if (reachedEnd)
        {
            _completion.TrySetResult(true);
        }

        return written;
    }

    /// <summary>
    /// 异步等待生成器被 Take 消费到末尾。
    /// 取消令牌只取消本次等待，不会改变生成器状态。
    /// </summary>
    public Task WaitUntilCompletedAsync(
        CancellationToken cancellationToken = default)
    {
        return cancellationToken.CanBeCanceled
            ? _completion.Task.WaitAsync(cancellationToken)
            : _completion.Task;
    }

    /// <summary>
    /// 同步阻塞，直到生成器被 Take 消费到末尾。
    /// </summary>
    public void WaitUntilCompleted(
        CancellationToken cancellationToken = default)
    {
        WaitUntilCompletedAsync(cancellationToken)
            .GetAwaiter()
            .GetResult();
    }
}
