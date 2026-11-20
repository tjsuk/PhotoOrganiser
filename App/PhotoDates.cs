using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace PhotoOrganiser;

public sealed record PhotoDate(DateTime Value, string Origin);

public static class PhotoDates
{
    // WIC paths vary by container. Try capture tags across containers before digitization tags.
    private static readonly (string Path, string Origin)[] Queries =
    [
        ("/app1/ifd/exif/{ushort=36867}", "EXIF DateTimeOriginal"),
        ("/ifd/exif/{ushort=36867}", "EXIF DateTimeOriginal"),
        ("/eXIf/ifd/exif/{ushort=36867}", "EXIF DateTimeOriginal"),
        ("/xmp/exif:DateTimeOriginal", "XMP DateTimeOriginal"),
        ("/app1/xmp/exif:DateTimeOriginal", "XMP DateTimeOriginal"),
        ("/xmp/photoshop:DateCreated", "XMP DateCreated"),
        ("/app1/xmp/photoshop:DateCreated", "XMP DateCreated"),
        ("System.Photo.DateTaken", "Windows Date Taken"),
        ("/app1/ifd/exif/{ushort=36868}", "EXIF DateTimeDigitized"),
        ("/ifd/exif/{ushort=36868}", "EXIF DateTimeDigitized"),
        ("/xmp/xmp:CreateDate", "XMP CreateDate"),
        ("/app1/xmp/xmp:CreateDate", "XMP CreateDate")
    ];
    // Accept EXIF timestamps and ISO-style XMP timestamps, including optional offsets.
    private static readonly string[] Formats =
    [
        "yyyy:MM:dd HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
        "yyyy-MM-dd HH:mm:ss.FFFFFFFK", "yyyy-MM-dd"
    ];

    public static PhotoDate Read(Stream stream, FileInfo file)
    {
        try
        {
            // Read metadata from the first frame without loading the complete pixel data.
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation,
                BitmapCacheOption.None);
            if (decoder.Frames[0].Metadata is BitmapMetadata metadata)
            {
                foreach (var query in Queries)
                {
                    if (ReadQuery(metadata, query.Path) is DateTime date)
                        return new(date, query.Origin);
                }

                if (Parse(metadata.DateTaken, CultureInfo.CurrentCulture) is DateTime taken)
                    return new(taken, "Windows Date Taken");
            }
        }
        catch (Exception error) when (IsMetadataError(error)) { }

        // Modification time often survives a copy, whereas creation time may be the import date.
        if (Valid(file.LastWriteTime))
            return new(file.LastWriteTime, "File modified");
        return new(file.CreationTime, "File created");
    }

    private static DateTime? ReadQuery(BitmapMetadata metadata, string path)
    {
        try
        {
            // Depending on the codec and tag, WIC can expose the date as text or a DateTime.
            return metadata.GetQuery(path) switch
            {
                string value => Parse(value),
                DateTime value when Valid(value) => value,
                _ => null
            };
        }
        catch (Exception error) when (IsMetadataError(error)) { return null; }
    }

    private static DateTime? Parse(string? value, CultureInfo? localized = null)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        value = value.Trim().TrimEnd('\0');
        // Keep the wall-clock date recorded by the camera even if an offset is present.
        if (DateTimeOffset.TryParseExact(value, Formats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var parsed) && Valid(parsed.DateTime))
            return parsed.DateTime;

        // Windows Date Taken can be localized. Keep explicit EXIF and XMP queries culture-independent.
        if (localized is not null && DateTime.TryParse(value, localized,
            DateTimeStyles.None, out var date) && Valid(date))
            return date;

        return null;
    }

    private static bool Valid(DateTime date) => date.Year >= 1602;

    private static bool IsMetadataError(Exception error) =>
        error is IOException or NotSupportedException or ArgumentException
            or COMException or InvalidOperationException or FormatException;
}
