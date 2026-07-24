using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using BackdropForCodex.Core.Media;
using BackdropForCodex.Core.Runtime;

namespace BackdropForCodex.Benchmarks;

internal static class Program
{
    private const int ReportSchemaVersion = 1;
    private const int DefaultIterations = 25;

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var options = BenchmarkOptions.Parse(args);
            if (options.ShowHelp)
            {
                WriteUsage();
                return 0;
            }

            if (!OperatingSystem.IsWindows())
            {
                Console.Error.WriteLine("This benchmark requires Windows.");
                return 2;
            }

            var report = await RunAsync(options).ConfigureAwait(false);
            var json = JsonSerializer.Serialize(report, SerializerOptions);

            Console.WriteLine(json);
            if (options.OutputPath is not null)
            {
                var outputPath = Path.GetFullPath(options.OutputPath);
                Directory.CreateDirectory(
                    Path.GetDirectoryName(outputPath)
                    ?? throw new IOException("The output path has no parent directory."));
                await File.WriteAllTextAsync(outputPath, json + Environment.NewLine)
                    .ConfigureAwait(false);
            }

            return 0;
        }
        catch (ArgumentException)
        {
            Console.Error.WriteLine("The benchmark arguments are invalid.");
            WriteUsage();
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(MapFailure(exception));
            return 1;
        }
    }

    private static async Task<LocalMediaBenchmarkReport> RunAsync(BenchmarkOptions options)
    {
        var reference = new MediaReference
        {
            MediaId = Guid.CreateVersion7(),
            SourceKind = MediaSourceKind.LocalFile,
            SourceIdentifier = Path.GetFullPath(options.MediaPath),
            LastKnownKind = MediaKind.None,
        };
        var provider = new LocalFileWallpaperSourceProvider();
        await using var pool = new SingleSlotPlaybackPool();

        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var privateBytesBefore = process.PrivateMemorySize64;

        var coldStart = Stopwatch.GetTimestamp();
        var coldLease = await provider.AcquireLeaseAsync(reference).ConfigureAwait(false);
        await pool.ActivateAsync(coldLease).ConfigureAwait(false);
        var coldMilliseconds = Stopwatch.GetElapsedTime(coldStart).TotalMilliseconds;
        var metadata = coldLease.Metadata;

        var warmDurations = new double[options.Iterations];
        for (var index = 0; index < warmDurations.Length; index++)
        {
            var started = Stopwatch.GetTimestamp();
            var lease = await provider.AcquireLeaseAsync(reference).ConfigureAwait(false);
            await pool.ActivateAsync(lease).ConfigureAwait(false);
            warmDurations[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        await pool.ReleaseAsync().ConfigureAwait(false);
        process.Refresh();
        var privateBytesAfter = process.PrivateMemorySize64;
        Array.Sort(warmDurations);

        return new LocalMediaBenchmarkReport(
            ReportSchemaVersion,
            DateTimeOffset.UtcNow,
            new BenchmarkEnvironment(
                Environment.OSVersion.Version.ToString(),
                RuntimeInformation.ProcessArchitecture.ToString(),
                RuntimeInformation.FrameworkDescription),
            new BenchmarkMedia(
                MediaSourceKind.LocalFile,
                metadata.Kind,
                metadata.Format,
                metadata.ContentLength),
            options.Iterations,
            coldMilliseconds,
            Percentile(warmDurations, 0.50),
            Percentile(warmDurations, 0.95),
            warmDurations[^1],
            privateBytesAfter - privateBytesBefore);
    }

    private static double Percentile(double[] sortedValues, double percentile)
    {
        var rank = (sortedValues.Length - 1) * percentile;
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper)
        {
            return sortedValues[lower];
        }

        var weight = rank - lower;
        return sortedValues[lower] + ((sortedValues[upper] - sortedValues[lower]) * weight);
    }

    private static void WriteUsage()
    {
        Console.Error.WriteLine(
            "Usage: BackdropForCodex.Benchmarks --media <absolute-or-relative-path> " +
            "[--iterations <1-1000>] [--output <json-path>]");
        Console.Error.WriteLine(
            "Runs a manual, path-free local media lease and single-slot activation benchmark. " +
            "It reports measurements only and does not enforce release thresholds.");
    }

    private static string MapFailure(Exception exception) => exception switch
    {
        MediaValidationException =>
            "Benchmark failed because the media source could not be validated.",
        UnauthorizedAccessException =>
            "Benchmark failed because an input or output file could not be accessed.",
        IOException =>
            "Benchmark failed because an input or output file could not be read or written.",
        _ => "Benchmark failed because of an unexpected error.",
    };

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed record BenchmarkOptions(
        string MediaPath,
        int Iterations,
        string? OutputPath,
        bool ShowHelp)
    {
        public static BenchmarkOptions Parse(string[] args)
        {
            if (args.Length == 1 && args[0] is "--help" or "-h")
            {
                return new BenchmarkOptions(string.Empty, DefaultIterations, null, ShowHelp: true);
            }

            string? mediaPath = null;
            string? outputPath = null;
            var iterations = DefaultIterations;
            for (var index = 0; index < args.Length; index++)
            {
                var argument = args[index];
                if (argument == "--media")
                {
                    mediaPath = ReadValue(args, ref index, argument);
                }
                else if (argument == "--iterations")
                {
                    var rawValue = ReadValue(args, ref index, argument);
                    if (!int.TryParse(rawValue, out iterations) ||
                        iterations is < 1 or > 1000)
                    {
                        throw new ArgumentException(
                            "--iterations must be an integer between 1 and 1000.");
                    }
                }
                else if (argument == "--output")
                {
                    outputPath = ReadValue(args, ref index, argument);
                }
                else
                {
                    throw new ArgumentException("An unknown argument was supplied.");
                }
            }

            if (string.IsNullOrWhiteSpace(mediaPath))
            {
                throw new ArgumentException("--media is required.");
            }

            return new BenchmarkOptions(mediaPath, iterations, outputPath, ShowHelp: false);
        }

        private static string ReadValue(
            string[] args,
            ref int index,
            string argument)
        {
            index++;
            if (index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
            {
                throw new ArgumentException($"{argument} requires a value.");
            }

            return args[index];
        }
    }
}

internal sealed record BenchmarkEnvironment(
    string OperatingSystemVersion,
    string ProcessArchitecture,
    string FrameworkDescription);

internal sealed record BenchmarkMedia(
    MediaSourceKind SourceKind,
    MediaKind MediaKind,
    MediaFormat Format,
    long ContentLength);

internal sealed record LocalMediaBenchmarkReport(
    int SchemaVersion,
    DateTimeOffset CapturedAtUtc,
    BenchmarkEnvironment Environment,
    BenchmarkMedia Media,
    int WarmIterations,
    double ColdActivateMilliseconds,
    double WarmActivateP50Milliseconds,
    double WarmActivateP95Milliseconds,
    double WarmActivateMaximumMilliseconds,
    long PrivateBytesDelta);
