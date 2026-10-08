using MusicAura.Core.Audio;
using MusicAura.Core.Engine;
using MusicAura.Core.Sync;

namespace MusicAura.Core.Tests;

public class TimelineTests
{
    private static float[] Ramp(long start, int frames)
    {
        var data = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            data[i * 2] = start + i;
            data[i * 2 + 1] = -(start + i);
        }

        return data;
    }

    [Fact]
    public void Buffer_reads_back_written_frames_across_wrap()
    {
        var buffer = new TimelineBuffer(100);
        for (var block = 0; block < 5; block++)
        {
            buffer.Append(Ramp(block * 70, 70));
        }

        Assert.Equal(350, buffer.WriteHead);
        Assert.Equal(250, buffer.OldestFrame);

        var dest = new float[40 * 2];
        Assert.Equal(40, buffer.Read(300, dest));
        for (var i = 0; i < 40; i++)
        {
            Assert.Equal(300 + i, dest[i * 2]);
            Assert.Equal(-(300 + i), dest[i * 2 + 1]);
        }
    }

    [Fact]
    public void Buffer_returns_silence_outside_valid_range()
    {
        var buffer = new TimelineBuffer(100);
        buffer.Append(Ramp(0, 50));
        var dest = new float[20 * 2];
        Array.Fill(dest, 9f);
        Assert.Equal(10, buffer.Read(40, dest));
        Assert.Equal(40, dest[0]);
        Assert.Equal(0, dest[10 * 2]);
        Assert.Equal(0, buffer.Read(-30, new float[10 * 2]));
    }

    [Fact]
    public void Moving_write_head_back_allows_rewriting_the_future()
    {
        var buffer = new TimelineBuffer(1000);
        buffer.Append(Ramp(0, 500));
        buffer.MoveWriteHead(300);
        Assert.Equal(300, buffer.WriteHead);
        buffer.Append(Ramp(1000, 10));
        var dest = new float[2];
        buffer.Read(300, dest);
        Assert.Equal(1000, dest[0]);

        buffer.MoveWriteHead(400);
        buffer.Read(350, dest);
        Assert.Equal(0, dest[0]); // padded with silence
    }

    [Fact]
    public void Gates_fade_out_hold_and_fade_in()
    {
        var gates = new GateList();
        gates.Add(100, 200, 10);
        var data = new float[300 * 2];
        Array.Fill(data, 1f);
        gates.Apply(0, data);

        Assert.Equal(1f, data[99 * 2]);
        Assert.InRange(data[104 * 2], 0.3f, 0.6f);
        Assert.Equal(0f, data[150 * 2]);
        Assert.InRange(data[204 * 2], 0.3f, 0.6f);
        Assert.Equal(1f, data[250 * 2]);
    }

    [Fact]
    public void Open_gate_mutes_until_closed()
    {
        var gates = new GateList();
        gates.AddOpen(50, 4);
        var data = new float[100 * 2];
        Array.Fill(data, 1f);
        gates.Apply(0, data);
        Assert.Equal(0f, data[99 * 2]);

        gates.CloseOpen(80);
        Array.Fill(data, 1f);
        gates.Apply(0, data);
        Assert.Equal(0f, data[79 * 2]);
        Assert.Equal(1f, data[90 * 2]);
        Assert.False(gates.HasOpenGate);
    }

    [Fact]
    public void Cursor_copies_exactly_at_unit_rate()
    {
        var buffer = new TimelineBuffer(10_000);
        buffer.Append(Ramp(0, 5000));
        var cursor = new SinkCursor(buffer, new SincKernel(), 44100, new RateController());
        var dest = new float[100 * 2];
        cursor.Render(dest, 1000, 0.001);
        for (var i = 0; i < 100; i++)
        {
            Assert.Equal(1000 + i, dest[i * 2], 3);
        }

        Assert.Equal(1100, cursor.Position, 6);
    }

    [Fact]
    public void Cursor_jumps_on_large_error_and_slews_small_ones()
    {
        var buffer = new TimelineBuffer(200_000);
        buffer.Append(Ramp(0, 150_000));
        var cursor = new SinkCursor(buffer, new SincKernel(), 44100, new RateController());
        var dest = new float[441 * 2];
        cursor.Render(dest, 10_000, 0.01);
        Assert.Equal(10_441, cursor.Position, 6);

        // 100 ms off: jump.
        cursor.Render(dest, 10_441 + 4410, 0.01);
        Assert.Equal(1, cursor.Resyncs);
        Assert.Equal(10_441 + 4410 + 441, cursor.Position, 6);

        // 10 ms off: no jump, the rate changes instead.
        var desired = cursor.Position + 441;
        cursor.Render(dest, desired, 0.01);
        Assert.Equal(1, cursor.Resyncs);
        Assert.True(cursor.LastStep > 1, "should speed up to catch up");
    }

    [Fact]
    public void Rate_controller_locks_onto_drifting_clock()
    {
        // Output DAC runs 150 ppm fast relative to the timeline and starts 20 ms off.
        var controller = new RateController();
        const double dt = 0.005;
        var drift = 150e-6;
        var error = 0.020;
        double maxLateError = 0;
        for (var step = 0; step < (int)(120 / dt); step++)
        {
            var rate = controller.Update(error, dt);
            // Content advances by 'rate' per output frame while the desired position advances by (1 + drift).
            error += (rate - (1 + drift)) * dt;
            if (step * dt > 60)
            {
                maxLateError = Math.Max(maxLateError, Math.Abs(error));
            }
        }

        Assert.True(maxLateError < 0.0005, $"residual error {maxLateError * 1000:0.000} ms");
        Assert.InRange(controller.LastAdjust, -drift - 2e-5, -drift + 2e-5);
    }

    [Fact]
    public void Segment_map_locates_track_positions()
    {
        var map = new SegmentMap();
        var a = new TrackInfo("a", "A", null, "a");
        var b = new TrackInfo("b", "B", null, "b");
        map.Start(1000, a, 0);
        map.EndCurrent(5000);
        map.Start(5000, b, 0);
        map.Start(8000, a, 44100);

        Assert.Equal((a, 500L), map.Locate(1500));
        Assert.Equal((b, 2000L), map.Locate(7000));
        Assert.Equal((a, 44100L + 100), map.Locate(8100));
        Assert.Null(map.Locate(500).Track);

        map.Start(6000, null, 0); // stop: truncates later segments
        Assert.Null(map.Locate(8100).Track);
        Assert.Equal((b, 500L), map.Locate(5500));
    }
}
