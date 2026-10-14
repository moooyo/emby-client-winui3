using System.Security;
using EmbyClient.App.Services;
using Xunit;

namespace EmbyClient.Platform.Tests;

public sealed class StartupRegistrationServiceTests
{
    private const string UnavailableError = "Startup registration is unavailable for this launch.";
    private const string ReadError = "Could not read startup registration. Try again.";
    private const string ChangeError = "Could not change startup registration. Try again.";
    private const string ConflictError = "Startup registration belongs to a different installation.";
    private static string ExecutablePath => Path.DirectorySeparatorChar == '\\'
        ? @"C:\Applications\Lumen Client\Lumen.exe" : "/applications/Lumen Client/Lumen.exe";
    private static string OwnedCommand => $"\"{ExecutablePath}\"";

    [Fact]
    public void Construction_does_not_query_the_platform_or_backend()
    {
        var backend = new FakeStartupRegistrationBackend();

        _ = new StartupRegistrationService(ExecutablePath, backend);
        _ = new StartupRegistrationService();

        Assert.Equal(0, backend.SupportCalls);
        Assert.Equal(0, backend.ReadCalls);
        Assert.Equal(0, backend.ChangeCalls);
    }

    [Fact]
    public void An_absent_registration_is_initially_off_and_reading_never_creates_it()
    {
        var backend = new FakeStartupRegistrationBackend();
        var service = CreateService(backend);

        var status = service.GetStatus();

        Assert.Equal(new StartupRegistrationStatus(true, false), status);
        Assert.False(backend.Value.Exists);
        Assert.Equal(1, backend.ReadCalls);
        Assert.Equal(0, backend.ChangeCalls);
        Assert.Equal(0, backend.MutationCalls);
    }

    [Fact]
    public async Task Enabling_quotes_the_entire_executable_path_and_reads_the_result_back()
    {
        var backend = new FakeStartupRegistrationBackend();
        var service = CreateService(backend);

        var status = await service.SetEnabledAsync(true, TestContext.Current.CancellationToken);

        Assert.Equal(new StartupRegistrationStatus(true, true), status);
        Assert.Equal(new StartupRegistrationValue(true, OwnedCommand), backend.Value);
        Assert.Equal(OwnedCommand, backend.LastCommand);
        Assert.Equal(2, backend.ReadCalls);
        Assert.Equal(1, backend.ChangeCalls);
        Assert.Equal(1, backend.MutationCalls);
    }

    [Fact]
    public async Task Disabling_removes_the_owned_registration_and_repeated_changes_are_idempotent()
    {
        var backend = new FakeStartupRegistrationBackend { Value = new(true, OwnedCommand) };
        var service = CreateService(backend);
        var token = TestContext.Current.CancellationToken;

        Assert.Equal(new StartupRegistrationStatus(true, true), await service.SetEnabledAsync(true, token));
        Assert.Equal(0, backend.ChangeCalls);
        Assert.Equal(new StartupRegistrationStatus(true, false), await service.SetEnabledAsync(false, token));
        Assert.Equal(new StartupRegistrationStatus(true, false), await service.SetEnabledAsync(false, token));

        Assert.False(backend.Value.Exists);
        Assert.Equal(1, backend.ChangeCalls);
        Assert.Equal(1, backend.MutationCalls);
    }

    [Fact]
    public async Task Command_path_casing_does_not_claim_a_second_installation()
    {
        var backend = new FakeStartupRegistrationBackend { Value = new(true, OwnedCommand.ToUpperInvariant()) };
        var service = CreateService(backend);

        Assert.Equal(new StartupRegistrationStatus(true, true), service.GetStatus());
        Assert.Equal(new StartupRegistrationStatus(true, false),
            await service.SetEnabledAsync(false, TestContext.Current.CancellationToken));
        Assert.Equal(1, backend.MutationCalls);
    }

    [Theory]
    [MemberData(nameof(InvalidExecutablePaths))]
    public async Task Missing_relative_nonexecutable_or_unquotable_paths_are_unavailable_without_backend_io(string? path)
    {
        var backend = new FakeStartupRegistrationBackend();
        var service = new StartupRegistrationService(path, backend);

        Assert.Equal(new StartupRegistrationStatus(false, false, UnavailableError), service.GetStatus());
        Assert.Equal(new StartupRegistrationStatus(false, false, UnavailableError),
            await service.SetEnabledAsync(true, TestContext.Current.CancellationToken));

        Assert.Equal(0, backend.ReadCalls);
        Assert.Equal(0, backend.ChangeCalls);
    }

    public static IEnumerable<object?[]> InvalidExecutablePaths()
    {
        yield return [null];
        yield return [""];
        yield return ["   "];
        yield return ["Lumen.exe"];
        yield return ["relative/Lumen.exe"];
        yield return [@"C:Lumen.exe"];
        yield return [@"\Lumen.exe"];
        yield return [ExecutablePath[..^4] + ".dll"];
        yield return [ExecutablePath[..^4] + ".exe --background"];
        yield return [ExecutablePath[..^4] + "\".exe"];
        yield return [ExecutablePath[..^4] + "\n.exe"];
        yield return [ExecutablePath[..^4] + "\0.exe"];
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unsupported_platforms_do_not_read_or_mutate_the_backend(bool enabled)
    {
        var backend = new FakeStartupRegistrationBackend { SupportsRegistration = false };
        var service = CreateService(backend);

        Assert.Equal(new StartupRegistrationStatus(false, false, UnavailableError), service.GetStatus());
        Assert.Equal(new StartupRegistrationStatus(false, false, UnavailableError),
            await service.SetEnabledAsync(enabled, TestContext.Current.CancellationToken));

        Assert.Equal(0, backend.ReadCalls);
        Assert.Equal(0, backend.ChangeCalls);
    }

    [Theory]
    [MemberData(nameof(ForeignCommands))]
    public async Task Foreign_values_are_not_claimed_or_changed_without_an_exact_quoted_command(string? command)
    {
        var backend = new FakeStartupRegistrationBackend { Value = new(true, command) };
        var service = CreateService(backend);

        Assert.Equal(new StartupRegistrationStatus(true, false, ConflictError), service.GetStatus());
        Assert.Equal(new StartupRegistrationStatus(true, false, ConflictError),
            await service.SetEnabledAsync(true, TestContext.Current.CancellationToken));
        Assert.Equal(new StartupRegistrationStatus(true, false, ConflictError),
            await service.SetEnabledAsync(false, TestContext.Current.CancellationToken));
        Assert.Equal(new StartupRegistrationValue(true, command), backend.Value);
        Assert.Equal(0, backend.ChangeCalls);
        Assert.Equal(0, backend.MutationCalls);
    }

    public static IEnumerable<object?[]> ForeignCommands()
    {
        yield return [null];
        yield return [""];
        yield return [ExecutablePath];
        yield return [OwnedCommand + " --background"];
        yield return [OwnedCommand + " "];
        yield return [$"\"{ExecutablePath.Replace("Lumen Client", "Another Client", StringComparison.Ordinal)}\""];
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Conflicting_registrations_are_preserved_for_both_requested_states(bool enabled)
    {
        var foreign = new StartupRegistrationValue(true, "\"different-installation.exe\"");
        var backend = new FakeStartupRegistrationBackend { Value = foreign };
        var service = CreateService(backend);

        var status = await service.SetEnabledAsync(enabled, TestContext.Current.CancellationToken);

        Assert.Equal(new StartupRegistrationStatus(true, false, ConflictError), status);
        Assert.Equal(foreign, backend.Value);
        Assert.Equal(0, backend.ChangeCalls);
        Assert.Equal(0, backend.MutationCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_conflict_introduced_before_mutation_is_preserved(bool enabled)
    {
        var foreign = new StartupRegistrationValue(true, "\"different-installation.exe\"");
        var backend = new FakeStartupRegistrationBackend
        {
            Value = enabled ? default : new(true, OwnedCommand)
        };
        backend.BeforeChange = () => backend.Value = foreign;
        var service = CreateService(backend);

        var status = await service.SetEnabledAsync(enabled, TestContext.Current.CancellationToken);

        Assert.Equal(new StartupRegistrationStatus(true, false, ConflictError), status);
        Assert.Equal(foreign, backend.Value);
        Assert.Equal(1, backend.ChangeCalls);
        Assert.Equal(0, backend.MutationCalls);
    }

    [Theory]
    [InlineData("IO")]
    [InlineData("Access")]
    [InlineData("Security")]
    public async Task Read_failures_are_reported_without_attempting_a_change(string failure)
    {
        var backend = new FakeStartupRegistrationBackend { ReadFailure = CreateFailure(failure) };
        var service = CreateService(backend);

        Assert.Equal(new StartupRegistrationStatus(true, false, ReadError), service.GetStatus());
        Assert.Equal(new StartupRegistrationStatus(true, false, ReadError),
            await service.SetEnabledAsync(true, TestContext.Current.CancellationToken));
        Assert.Equal(0, backend.ChangeCalls);
        Assert.Equal(0, backend.MutationCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Write_failures_report_the_state_that_can_be_read_back(bool enabled)
    {
        var original = enabled ? default : new StartupRegistrationValue(true, OwnedCommand);
        var backend = new FakeStartupRegistrationBackend
        {
            Value = original,
            ChangeFailure = new UnauthorizedAccessException("Synthetic startup write failure.")
        };
        var service = CreateService(backend);

        var status = await service.SetEnabledAsync(enabled, TestContext.Current.CancellationToken);

        Assert.Equal(new StartupRegistrationStatus(true, !enabled, ChangeError), status);
        Assert.Equal(original, backend.Value);
        Assert.Equal(0, backend.MutationCalls);
    }

    [Fact]
    public async Task A_failure_after_writing_does_not_report_the_previous_disabled_state()
    {
        var backend = new FakeStartupRegistrationBackend
        {
            AfterChangeFailure = new IOException("Synthetic failure after startup write.")
        };
        var service = CreateService(backend);

        var status = await service.SetEnabledAsync(true, TestContext.Current.CancellationToken);

        Assert.Equal(new StartupRegistrationStatus(true, true, ChangeError), status);
        Assert.Equal(new StartupRegistrationValue(true, OwnedCommand), backend.Value);
        Assert.Equal(1, backend.MutationCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_successful_backend_call_is_not_success_until_the_requested_state_is_verified(bool enabled)
    {
        var backend = new FakeStartupRegistrationBackend
        {
            Value = enabled ? default : new(true, OwnedCommand),
            IgnoreChanges = true
        };
        var service = CreateService(backend);

        var status = await service.SetEnabledAsync(enabled, TestContext.Current.CancellationToken);

        Assert.Equal(new StartupRegistrationStatus(true, !enabled, ChangeError), status);
        Assert.Equal(1, backend.ChangeCalls);
        Assert.Equal(0, backend.MutationCalls);
    }

    [Fact]
    public async Task A_foreign_command_seen_during_readback_is_not_removed_or_claimed()
    {
        var foreign = new StartupRegistrationValue(true, "\"different-installation.exe\"");
        var backend = new FakeStartupRegistrationBackend();
        backend.AfterChange = () => backend.Value = foreign;
        var service = CreateService(backend);

        var status = await service.SetEnabledAsync(true, TestContext.Current.CancellationToken);

        Assert.Equal(new StartupRegistrationStatus(true, false, ConflictError), status);
        Assert.Equal(foreign, backend.Value);
        Assert.Equal(1, backend.MutationCalls);
    }

    [Fact]
    public async Task A_readback_failure_is_reported_even_after_the_command_was_written()
    {
        var backend = new FakeStartupRegistrationBackend();
        backend.AfterChange = () => backend.ReadFailure = new IOException("Synthetic startup readback failure.");
        var service = CreateService(backend);

        var status = await service.SetEnabledAsync(true, TestContext.Current.CancellationToken);

        Assert.Equal(new StartupRegistrationStatus(true, false, ReadError), status);
        Assert.Equal(new StartupRegistrationValue(true, OwnedCommand), backend.Value);
        Assert.Equal(1, backend.MutationCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Precanceled_requests_do_not_even_query_the_backend(bool enabled)
    {
        var backend = new FakeStartupRegistrationBackend { Value = new(true, OwnedCommand) };
        var service = CreateService(backend);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SetEnabledAsync(enabled, canceled.Token));

        Assert.Equal(0, backend.SupportCalls);
        Assert.Equal(0, backend.ReadCalls);
        Assert.Equal(0, backend.ChangeCalls);
        Assert.Equal(new StartupRegistrationValue(true, OwnedCommand), backend.Value);
    }

    [Fact]
    public async Task Cancellation_after_the_initial_read_prevents_mutation()
    {
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var backend = new FakeStartupRegistrationBackend { BeforeRead = canceled.Cancel };
        var service = CreateService(backend);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SetEnabledAsync(true, canceled.Token));

        Assert.False(backend.Value.Exists);
        Assert.Equal(0, backend.ChangeCalls);
        Assert.Equal(0, backend.MutationCalls);
    }

    [Fact]
    public async Task Cancellation_in_the_backend_before_writing_preserves_the_absent_value()
    {
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var backend = new FakeStartupRegistrationBackend { BeforeChange = canceled.Cancel };
        var service = CreateService(backend);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SetEnabledAsync(true, canceled.Token));

        Assert.False(backend.Value.Exists);
        Assert.Equal(1, backend.ChangeCalls);
        Assert.Equal(0, backend.MutationCalls);
    }

    [Fact]
    public async Task Cancellation_after_writing_still_returns_the_verified_result()
    {
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var backend = new FakeStartupRegistrationBackend { AfterChange = canceled.Cancel };
        var service = CreateService(backend);

        var status = await service.SetEnabledAsync(true, canceled.Token);

        Assert.True(canceled.IsCancellationRequested);
        Assert.Equal(new StartupRegistrationStatus(true, true), status);
        Assert.Equal(1, backend.MutationCalls);
    }

    [Fact(Timeout = 15000)]
    public async Task Cancellation_while_waiting_for_another_action_never_mutates_or_releases_its_gate()
    {
        var token = TestContext.Current.CancellationToken;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = CreateBlockingBackend(entered, release, token);
        var service = CreateService(backend);
        var otherService = CreateService(backend);
        var enabling = Task.Run(() => service.SetEnabledAsync(true, token), token);
        try
        {
            await entered.Task.WaitAsync(token);
            using var canceled = CancellationTokenSource.CreateLinkedTokenSource(token);
            var pending = otherService.SetEnabledAsync(false, canceled.Token);
            Assert.False(pending.IsCompleted);

            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.False(enabling.IsCompleted);
            Assert.Equal(1, backend.ReadCalls);
            Assert.Equal(0, backend.ChangeCalls);
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.Equal(new StartupRegistrationStatus(true, true), await enabling);
        Assert.Equal(new StartupRegistrationStatus(true, false), await otherService.SetEnabledAsync(false, token));
        Assert.Equal(2, backend.MutationCalls);
        Assert.Equal(1, backend.MaximumConcurrentAccesses);
    }

    [Fact(Timeout = 15000)]
    public async Task A_status_read_serializes_changes_from_other_service_instances()
    {
        var token = TestContext.Current.CancellationToken;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = CreateBlockingBackend(entered, release, token);
        var enablingService = CreateService(backend);
        var readingService = CreateService(backend);
        var reading = Task.Run(() => readingService.GetStatus(), token);
        Task<StartupRegistrationStatus>? enabling = null;
        try
        {
            await entered.Task.WaitAsync(token);
            enabling = enablingService.SetEnabledAsync(true, token);
            Assert.False(enabling.IsCompleted);
            Assert.False(reading.IsCompleted);
            Assert.Equal(1, backend.ReadCalls);
            Assert.Equal(0, backend.ChangeCalls);
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.Equal(new StartupRegistrationStatus(true, false), await reading);
        Assert.Equal(new StartupRegistrationStatus(true, true), await enabling!);
        Assert.Equal(new StartupRegistrationStatus(true, true), readingService.GetStatus());
        Assert.Equal(1, backend.MutationCalls);
        Assert.Equal(1, backend.MaximumConcurrentAccesses);
    }

    private static StartupRegistrationService CreateService(FakeStartupRegistrationBackend backend) =>
        new(ExecutablePath, backend);

    private static Exception CreateFailure(string failure) => failure switch
    {
        "IO" => new IOException("Synthetic startup read failure."),
        "Access" => new UnauthorizedAccessException("Synthetic startup access failure."),
        "Security" => new SecurityException("Synthetic startup security failure."),
        _ => throw new ArgumentOutOfRangeException(nameof(failure))
    };

    private static FakeStartupRegistrationBackend CreateBlockingBackend(
        TaskCompletionSource entered, TaskCompletionSource release, CancellationToken token)
    {
        var reads = 0;
        return new FakeStartupRegistrationBackend
        {
            BeforeRead = () =>
            {
                if (Interlocked.Increment(ref reads) != 1) return;
                entered.TrySetResult();
                release.Task.WaitAsync(token).GetAwaiter().GetResult();
            }
        };
    }

    private sealed class FakeStartupRegistrationBackend : IStartupRegistrationBackend
    {
        private int _concurrentAccesses;
        private int _maximumConcurrentAccesses;
        public bool SupportsRegistration { get; set; } = true;
        public StartupRegistrationValue Value { get; set; }
        public Exception? ReadFailure { get; set; }
        public Exception? ChangeFailure { get; set; }
        public Exception? AfterChangeFailure { get; set; }
        public Action? BeforeRead { get; set; }
        public Action? BeforeChange { get; set; }
        public Action? AfterChange { get; set; }
        public bool IgnoreChanges { get; set; }
        public string? LastCommand { get; private set; }
        public int SupportCalls { get; private set; }
        public int ReadCalls { get; private set; }
        public int ChangeCalls { get; private set; }
        public int MutationCalls { get; private set; }
        public int MaximumConcurrentAccesses => Volatile.Read(ref _maximumConcurrentAccesses);
        public bool IsSupported { get { SupportCalls++; return SupportsRegistration; } }

        public StartupRegistrationValue ReadValue()
        {
            Enter();
            try
            {
                ReadCalls++;
                BeforeRead?.Invoke();
                if (ReadFailure is not null) throw ReadFailure;
                return Value;
            }
            finally
            {
                Interlocked.Decrement(ref _concurrentAccesses);
            }
        }

        public bool TrySetEnabled(string command, bool enabled, CancellationToken cancellationToken)
        {
            Enter();
            try
            {
                ChangeCalls++;
                LastCommand = command;
                BeforeChange?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
                if (Value.Exists && !string.Equals(Value.Command, command, StringComparison.OrdinalIgnoreCase)) return false;
                if (ChangeFailure is not null) throw ChangeFailure;
                if (Value.Exists == enabled || IgnoreChanges) return true;
                Value = enabled ? new(true, command) : default;
                MutationCalls++;
                AfterChange?.Invoke();
                if (AfterChangeFailure is not null) throw AfterChangeFailure;
                return true;
            }
            finally
            {
                Interlocked.Decrement(ref _concurrentAccesses);
            }
        }

        private void Enter()
        {
            var concurrent = Interlocked.Increment(ref _concurrentAccesses);
            var maximum = Volatile.Read(ref _maximumConcurrentAccesses);
            while (concurrent > maximum)
            {
                var observed = Interlocked.CompareExchange(ref _maximumConcurrentAccesses, concurrent, maximum);
                if (observed == maximum) return;
                maximum = observed;
            }
        }
    }
}
