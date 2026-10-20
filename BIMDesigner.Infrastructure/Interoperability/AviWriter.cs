using System.Text;

namespace BIMDesigner.Infrastructure.Interoperability;

/// <summary>
/// Writes a video as a Motion-JPEG AVI: a RIFF file whose frames are each a JPEG picture, with an
/// index at the end. Every frame stands on its own, so any frame can be shown first, and the
/// common players - Windows' own, VLC - play it. The frames are written as they come; the
/// counts at the head are filled in when it is finished.
/// </summary>
public sealed class AviWriter : IDisposable
{
    private readonly Stream _stream;
    private readonly BinaryWriter _writer;
    private readonly int _width;
    private readonly int _height;
    private readonly double _framesPerSecond;
    private readonly List<(long Offset, int Size)> _index = new();
    private readonly long _riffSize;
    private readonly long _totalFrames;
    private readonly long _streamLength;
    private readonly long _moviSize;
    private readonly long _moviStart;
    private readonly long _suggestedBuffer;
    private readonly long _streamBuffer;
    private int _largest;
    private bool _finished;

    public AviWriter(Stream stream, int width, int height, double framesPerSecond)
    {
        (_stream, _width, _height, _framesPerSecond) = (stream, width, height, framesPerSecond);
        _writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        // RIFF 'AVI ' - its size filled in at the end.
        Tag("RIFF");
        _riffSize = Placeholder();
        Tag("AVI ");

        // The header list: the main header, then one stream - video, Motion-JPEG.
        Tag("LIST");
        _writer.Write(4 + 8 + 56 + 8 + 4 + 8 + 56 + 8 + 40);
        Tag("hdrl");

        Tag("avih");
        _writer.Write(56);
        _writer.Write((int)Math.Round(1_000_000 / framesPerSecond)); // microseconds a frame
        _writer.Write(0);                                              // most bytes a second: not said
        _writer.Write(0);                                              // padding granularity
        _writer.Write(0x10);                                           // has an index
        _totalFrames = Placeholder();
        _writer.Write(0);                                              // initial frames
        _writer.Write(1);                                              // streams
        _suggestedBuffer = Placeholder();
        _writer.Write(width);
        _writer.Write(height);
        _writer.Write(new byte[16]);                                   // reserved

        Tag("LIST");
        _writer.Write(4 + 8 + 56 + 8 + 40);
        Tag("strl");

        Tag("strh");
        _writer.Write(56);
        Tag("vids");
        Tag("MJPG");
        _writer.Write(0);                                              // flags
        _writer.Write((short)0);                                       // priority
        _writer.Write((short)0);                                       // language
        _writer.Write(0);                                              // initial frames
        var (scale, rate) = Rate(framesPerSecond);
        _writer.Write(scale);
        _writer.Write(rate);
        _writer.Write(0);                                              // start
        _streamLength = Placeholder();
        _streamBuffer = Placeholder();
        _writer.Write(-1);                                             // quality: the default
        _writer.Write(0);                                              // sample size: frames vary
        _writer.Write((short)0);
        _writer.Write((short)0);
        _writer.Write((short)width);
        _writer.Write((short)height);

        Tag("strf");
        _writer.Write(40);
        _writer.Write(40);                                             // BITMAPINFOHEADER
        _writer.Write(width);
        _writer.Write(height);
        _writer.Write((short)1);                                       // planes
        _writer.Write((short)24);                                      // bits a pixel
        Tag("MJPG");
        _writer.Write(width * height * 3);
        _writer.Write(0);
        _writer.Write(0);
        _writer.Write(0);
        _writer.Write(0);

        // The frames.
        Tag("LIST");
        _moviSize = Placeholder();
        _moviStart = _stream.Position;
        Tag("movi");
    }

    /// <summary>How many frames have been written.</summary>
    public int Frames => _index.Count;

    /// <summary>A frame: one JPEG picture, the size the video is.</summary>
    public void Add(byte[] jpeg)
    {
        if (_finished) throw new InvalidOperationException("The video is finished.");

        _index.Add((_stream.Position - _moviStart, jpeg.Length));
        Tag("00dc");
        _writer.Write(jpeg.Length);
        _writer.Write(jpeg);
        if (jpeg.Length % 2 == 1) _writer.Write((byte)0);
        _largest = Math.Max(_largest, jpeg.Length);
    }

    /// <summary>The index written after the frames, and the counts at the head filled in.</summary>
    public void Finish()
    {
        if (_finished) return;
        _finished = true;

        var moviEnd = _stream.Position;
        Tag("idx1");
        _writer.Write(_index.Count * 16);
        foreach (var (offset, size) in _index)
        {
            Tag("00dc");
            _writer.Write(0x10);                                       // a key frame: every one is
            _writer.Write((int)offset);
            _writer.Write(size);
        }

        var end = _stream.Position;
        Fill(_riffSize, end - 8);
        Fill(_totalFrames, _index.Count);
        Fill(_streamLength, _index.Count);
        Fill(_suggestedBuffer, _largest + 8);
        Fill(_streamBuffer, _largest + 8);
        Fill(_moviSize, moviEnd - _moviStart);
        _stream.Position = end;
        _writer.Flush();
    }

    public void Dispose()
    {
        Finish();
        _writer.Dispose();
    }

    /// <summary>A frame rate as a whole-number rate over a scale: 15 as 15/1, 29.97 as 2997/100.</summary>
    private static (int Scale, int Rate) Rate(double framesPerSecond) =>
        Math.Abs(framesPerSecond - Math.Round(framesPerSecond)) < 1e-9 ? (1, (int)Math.Round(framesPerSecond)) : (100, (int)Math.Round(framesPerSecond * 100));

    private void Tag(string fourCc) => _writer.Write(Encoding.ASCII.GetBytes(fourCc));

    private long Placeholder()
    {
        var at = _stream.Position;
        _writer.Write(0);
        return at;
    }

    private void Fill(long at, long value)
    {
        _writer.Flush();
        _stream.Position = at;
        _writer.Write((int)value);
    }
}
