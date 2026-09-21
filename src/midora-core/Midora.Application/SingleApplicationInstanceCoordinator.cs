using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Midora.Common;

namespace Midora.Application;

public sealed class ApplicationStartupRequest
{
    private readonly ReadOnlyCollection<string> _arguments;

    public ApplicationStartupRequest(string workingDirectory, IEnumerable<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        if (!Path.IsPathFullyQualified(workingDirectory))
        {
            throw new ArgumentException(
                "The startup working directory must be fully qualified.",
                nameof(workingDirectory));
        }
        ArgumentNullException.ThrowIfNull(arguments);

        List<string> snapshot = [];
        foreach (string? argument in arguments)
        {
            snapshot.Add(argument
                ?? throw new ArgumentException("Startup arguments cannot contain null.", nameof(arguments)));
        }

        WorkingDirectory = workingDirectory;
        _arguments = Array.AsReadOnly(snapshot.ToArray());
    }

    public string WorkingDirectory { get; }
    public IReadOnlyList<string> Arguments => _arguments;
}

public enum ApplicationInstanceStartOutcome
{
    Primary,
    Forwarded
}

public enum ApplicationInstanceForwardingFailure
{
    PrimaryUnavailable,
    QueueFull,
    ProtocolRejected
}

public sealed class ApplicationInstanceForwardingException : IOException
{
    internal ApplicationInstanceForwardingException(
        ApplicationInstanceForwardingFailure failure,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
    }

    public ApplicationInstanceForwardingFailure Failure { get; }
}

public sealed class ApplicationInstanceStartResult
{
    private ApplicationInstanceStartResult(
        ApplicationInstanceStartOutcome outcome,
        SingleApplicationInstanceCoordinator? primaryInstance)
    {
        Outcome = outcome;
        PrimaryInstance = primaryInstance;
    }

    public ApplicationInstanceStartOutcome Outcome { get; }
    public SingleApplicationInstanceCoordinator? PrimaryInstance { get; }
    public bool ShouldExit => Outcome == ApplicationInstanceStartOutcome.Forwarded;

    internal static ApplicationInstanceStartResult Primary(
        SingleApplicationInstanceCoordinator instance) =>
        new(ApplicationInstanceStartOutcome.Primary, instance);

    internal static ApplicationInstanceStartResult Forwarded() =>
        new(ApplicationInstanceStartOutcome.Forwarded, null);
}

public sealed class SingleApplicationInstanceCoordinator : IAsyncDisposable
{
    public const int MaximumPayloadBytes = 1024 * 1024;
    public const int MaximumArguments = 256;
    public const int MaximumPendingRequests = 64;
    private const int MaximumApplicationIdLength = 256;
    private const int PipeBufferBytes = 4096;
    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ClientRequestTimeout = TimeSpan.FromSeconds(5);
    private readonly object _disposeSync = new();
    private readonly Mutex _instanceMarker;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Channel<ApplicationStartupRequest> _requests;
    private readonly Task _listenerTask;
    private Task? _disposeTask;

    private SingleApplicationInstanceCoordinator(Mutex instanceMarker, string pipeName)
    {
        _instanceMarker = instanceMarker;
        PipeName = pipeName;
        _requests = Channel.CreateBounded<ApplicationStartupRequest>(
            new BoundedChannelOptions(MaximumPendingRequests)
            {
                AllowSynchronousContinuations = false,
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true
            });
        NamedPipeServerStream initialServer = CreateServer(pipeName);
        _listenerTask = ListenAsync(initialServer);
    }

    public Task Completion => _listenerTask;
    internal string PipeName { get; }

    public static async Task<ApplicationInstanceStartResult> StartOrForwardAsync(
        string applicationId,
        ApplicationStartupRequest startupRequest,
        TimeSpan? connectTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationId);
        if (applicationId.Length > MaximumApplicationIdLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(applicationId),
                $"The application ID cannot exceed {MaximumApplicationIdLength} UTF-16 code units.");
        }
        ArgumentNullException.ThrowIfNull(startupRequest);
        TimeSpan timeout = connectTimeout ?? DefaultConnectTimeout;
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(connectTimeout));
        }

        byte[] frame = ApplicationInstanceProtocolV1.Serialize(startupRequest);
        string objectSuffix = CreateObjectSuffix(applicationId);
        string mutexName = CreateMutexName(objectSuffix);
        string pipeName = CreatePipeName(objectSuffix);
        Exception? lastForwardingError = null;

        // Claim the primary slot. A named mutex is released by the operating system when its
        // process dies on every platform, so a crashed instance cannot block the next launch.
        Mutex marker = new(initiallyOwned: false, mutexName, out bool createdNew);
        if (createdNew)
        {
            try
            {
                return ApplicationInstanceStartResult.Primary(
                    new SingleApplicationInstanceCoordinator(marker, pipeName));
            }
            catch
            {
                marker.Dispose();
                throw;
            }
        }
        marker.Dispose();

        // Forward to the primary, retrying until the caller's timeout expires. The primary
        // serves one connection at a time and re-creates its listener afterwards; Unix domain
        // sockets drop connections queued on a listener that is being replaced, so a single
        // retry is not enough there. Windows pipes queue the same way but keep the client
        // waiting instead of failing it.
        long deadline = Environment.TickCount64 + checked((long)Math.Ceiling(timeout.TotalMilliseconds));
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan remaining = TimeSpan.FromMilliseconds(
                Math.Max(1, deadline - Environment.TickCount64));
            try
            {
                await ApplicationInstanceProtocolV1.ForwardAsync(
                    pipeName,
                    frame,
                    remaining,
                    cancellationToken).ConfigureAwait(false);
                return ApplicationInstanceStartResult.Forwarded();
            }
            catch (ApplicationInstanceForwardingException exception)
                when (exception.Failure is ApplicationInstanceForwardingFailure.QueueFull
                    or ApplicationInstanceForwardingFailure.ProtocolRejected)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or TimeoutException)
            {
                lastForwardingError = exception;
                if (Environment.TickCount64 >= deadline)
                {
                    break;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        throw new ApplicationInstanceForwardingException(
            ApplicationInstanceForwardingFailure.PrimaryUnavailable,
            "The existing Midora application instance did not accept the startup request.",
            lastForwardingError);
    }

    public ValueTask<ApplicationStartupRequest> ReceiveAsync(
        CancellationToken cancellationToken = default) =>
        _requests.Reader.ReadAsync(cancellationToken);

    public IAsyncEnumerable<ApplicationStartupRequest> ReadAllAsync(
        CancellationToken cancellationToken = default) =>
        _requests.Reader.ReadAllAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        _shutdown.Cancel();
        try
        {
            await _listenerTask.ConfigureAwait(false);
        }
        finally
        {
            _instanceMarker.Dispose();
            _shutdown.Dispose();
        }
    }

    private async Task ListenAsync(NamedPipeServerStream server)
    {
        Exception? terminalError = null;
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                using (server)
                {
                    try
                    {
                        await server.WaitForConnectionAsync(_shutdown.Token).ConfigureAwait(false);
                        using CancellationTokenSource requestTimeout =
                            CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                        requestTimeout.CancelAfter(ClientRequestTimeout);
                        await ServeConnectionAsync(server, requestTimeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        // A connected client that never completes its frame cannot monopolize
                        // the single ordered launch channel indefinitely.
                    }
                    catch (IOException) when (!_shutdown.IsCancellationRequested)
                    {
                        // A client can disappear at any byte boundary. The next launch must still
                        // be able to connect to a fresh server instance.
                    }
                }

                if (!_shutdown.IsCancellationRequested)
                {
                    server = CreateServer(PipeName);
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            terminalError = exception;
            throw;
        }
        finally
        {
            server.Dispose();
            _requests.Writer.TryComplete(terminalError);
        }
    }

    private async Task ServeConnectionAsync(
        NamedPipeServerStream server,
        CancellationToken cancellationToken)
    {
        ApplicationInstanceProtocolResponse response;
        try
        {
            ApplicationStartupRequest request =
                await ApplicationInstanceProtocolV1.ReadAsync(server, cancellationToken)
                    .ConfigureAwait(false);
            response = _requests.Writer.TryWrite(request)
                ? ApplicationInstanceProtocolResponse.Accepted
                : ApplicationInstanceProtocolResponse.QueueFull;
        }
        catch (InvalidDataException)
        {
            response = ApplicationInstanceProtocolResponse.ProtocolRejected;
        }

        await ApplicationInstanceProtocolV1.WriteResponseAsync(
            server,
            response,
            cancellationToken).ConfigureAwait(false);
    }

    private static NamedPipeServerStream CreateServer(string pipeName) =>
        new(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            PipeBufferBytes,
            PipeBufferBytes);

    internal static string CreateObjectSuffix(string applicationId)
    {
        // 128 bits of the identity hash keep the suffix short enough for the Unix named-pipe
        // socket path budget (macOS allows about 104 bytes for sun_path, including the
        // CoreFxPipe_ prefix) while staying collision-resistant across application identities.
        string identity = $"Midora.Application.Instance.v1\0{applicationId}\0{Process.GetCurrentProcess().SessionId}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }

    internal static string CreateMutexName(string objectSuffix) =>
        $"Local\\Midora.Application.Instance.v1.{objectSuffix}";

    internal static string CreatePipeName(string objectSuffix) =>
        MidoraInterprocessPipeName.Create($"Midora.Application.Instance.v1.{objectSuffix}");
}

internal enum ApplicationInstanceProtocolResponse : byte
{
    Accepted = 1,
    QueueFull = 2,
    ProtocolRejected = 3
}

internal static class ApplicationInstanceProtocolV1
{
    internal const uint Magic = 0x3141494D;
    internal const ushort Version = 1;
    internal const int HeaderSize = 16;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static byte[] Serialize(ApplicationStartupRequest request)
    {
        if (request.Arguments.Count > SingleApplicationInstanceCoordinator.MaximumArguments)
        {
            throw new ArgumentException(
                $"A startup request cannot contain more than {SingleApplicationInstanceCoordinator.MaximumArguments} arguments.",
                nameof(request));
        }

        int payloadLength = CalculatePayloadLength(request);
        using MemoryStream payload = new(payloadLength);
        using (BinaryWriter writer = new(payload, StrictUtf8, leaveOpen: true))
        {
            WriteString(writer, request.WorkingDirectory);
            writer.Write(request.Arguments.Count);
            foreach (string argument in request.Arguments)
            {
                WriteString(writer, argument);
            }
        }
        byte[] frame = GC.AllocateUninitializedArray<byte>(checked(HeaderSize + payloadLength));
        Span<byte> header = frame.AsSpan(0, HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], 0);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], payloadLength);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], 0);
        payload.Position = 0;
        _ = payload.Read(frame, HeaderSize, payloadLength);
        return frame;
    }

    public static async Task ForwardAsync(
        string pipeName,
        byte[] frame,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using NamedPipeClientStream client = new(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        int timeoutMilliseconds = checked((int)Math.Ceiling(timeout.TotalMilliseconds));
        await client.ConnectAsync(timeoutMilliseconds, cancellationToken).ConfigureAwait(false);
        await client.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await client.FlushAsync(cancellationToken).ConfigureAwait(false);

        byte[] responseBuffer = new byte[1];
        await ReadExactlyAsync(client, responseBuffer, cancellationToken).ConfigureAwait(false);
        ApplicationInstanceProtocolResponse response =
            (ApplicationInstanceProtocolResponse)responseBuffer[0];
        switch (response)
        {
            case ApplicationInstanceProtocolResponse.Accepted:
                return;
            case ApplicationInstanceProtocolResponse.QueueFull:
                throw new ApplicationInstanceForwardingException(
                    ApplicationInstanceForwardingFailure.QueueFull,
                    "The existing Midora application instance startup-request queue is full.");
            case ApplicationInstanceProtocolResponse.ProtocolRejected:
                throw new ApplicationInstanceForwardingException(
                    ApplicationInstanceForwardingFailure.ProtocolRejected,
                    "The existing Midora application instance rejected the startup protocol.");
            default:
                throw new ApplicationInstanceForwardingException(
                    ApplicationInstanceForwardingFailure.ProtocolRejected,
                    "The existing Midora application instance returned an unknown startup response.");
        }
    }

    public static async Task<ApplicationStartupRequest> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        byte[] header = new byte[HeaderSize];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        ReadOnlySpan<byte> headerSpan = header;
        if (BinaryPrimitives.ReadUInt32LittleEndian(headerSpan) != Magic
            || BinaryPrimitives.ReadUInt16LittleEndian(headerSpan[4..]) != Version
            || BinaryPrimitives.ReadUInt16LittleEndian(headerSpan[6..]) != 0
            || BinaryPrimitives.ReadInt32LittleEndian(headerSpan[12..]) != 0)
        {
            throw new InvalidDataException("Invalid application-instance protocol header.");
        }
        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(headerSpan[8..]);
        if (payloadLength < 0
            || payloadLength > SingleApplicationInstanceCoordinator.MaximumPayloadBytes)
        {
            throw new InvalidDataException("Invalid application-instance protocol payload length.");
        }

        byte[] payload = GC.AllocateUninitializedArray<byte>(payloadLength);
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        int offset = 0;
        string workingDirectory = ReadString(payload, ref offset);
        int argumentCount = ReadInt32(payload, ref offset);
        if (argumentCount < 0
            || argumentCount > SingleApplicationInstanceCoordinator.MaximumArguments)
        {
            throw new InvalidDataException("Invalid application-instance protocol argument count.");
        }
        string[] arguments = new string[argumentCount];
        for (int i = 0; i < arguments.Length; i++)
        {
            arguments[i] = ReadString(payload, ref offset);
        }
        if (offset != payload.Length)
        {
            throw new InvalidDataException("Application-instance protocol payload has trailing bytes.");
        }

        try
        {
            return new ApplicationStartupRequest(workingDirectory, arguments);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("Invalid application-instance startup request.", exception);
        }
    }

    public static async Task WriteResponseAsync(
        Stream stream,
        ApplicationInstanceProtocolResponse response,
        CancellationToken cancellationToken)
    {
        byte[] responseBuffer = [(byte)response];
        await stream.WriteAsync(responseBuffer, cancellationToken).ConfigureAwait(false);
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        int byteCount = StrictUtf8.GetByteCount(value);
        if (byteCount > SingleApplicationInstanceCoordinator.MaximumPayloadBytes)
        {
            throw new ArgumentException("A startup-request string exceeds the protocol limit.");
        }
        writer.Write(byteCount);
        writer.Write(StrictUtf8.GetBytes(value));
    }

    private static int CalculatePayloadLength(ApplicationStartupRequest request)
    {
        try
        {
            long payloadLength = sizeof(int)
                + StrictUtf8.GetByteCount(request.WorkingDirectory)
                + sizeof(int);
            foreach (string argument in request.Arguments)
            {
                payloadLength = checked(
                    payloadLength + sizeof(int) + StrictUtf8.GetByteCount(argument));
                if (payloadLength > SingleApplicationInstanceCoordinator.MaximumPayloadBytes)
                {
                    throw new ArgumentException(
                        $"A startup request payload cannot exceed {SingleApplicationInstanceCoordinator.MaximumPayloadBytes} bytes.",
                        nameof(request));
                }
            }
            if (payloadLength > SingleApplicationInstanceCoordinator.MaximumPayloadBytes)
            {
                throw new ArgumentException(
                    $"A startup request payload cannot exceed {SingleApplicationInstanceCoordinator.MaximumPayloadBytes} bytes.",
                    nameof(request));
            }
            return checked((int)payloadLength);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException(
                "A startup request contains invalid Unicode text.",
                nameof(request),
                exception);
        }
        catch (OverflowException exception)
        {
            throw new ArgumentException(
                "A startup request payload exceeds the protocol limit.",
                nameof(request),
                exception);
        }
    }

    private static string ReadString(byte[] payload, ref int offset)
    {
        int byteCount = ReadInt32(payload, ref offset);
        if (byteCount < 0 || byteCount > payload.Length - offset)
        {
            throw new InvalidDataException("Invalid application-instance protocol string length.");
        }
        try
        {
            string value = StrictUtf8.GetString(payload, offset, byteCount);
            offset += byteCount;
            return value;
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException(
                "Application-instance protocol contains invalid UTF-8.",
                exception);
        }
    }

    private static int ReadInt32(byte[] payload, ref int offset)
    {
        if (payload.Length - offset < sizeof(int))
        {
            throw new InvalidDataException("Application-instance protocol payload is truncated.");
        }
        int value = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(offset, sizeof(int)));
        offset += sizeof(int);
        return value;
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[offset..], cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    "Application-instance protocol ended before the expected byte count.");
            }
            offset += read;
        }
    }
}
