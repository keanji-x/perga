using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Perga.Host.Auth;
using Perga.Host.IntegrationTests.Fixtures;
using static Perga.Host.IntegrationTests.AuthHttpIntegrationTests;

namespace Perga.Host.IntegrationTests;

public sealed class LoginRateLimitIntegrationTests
{
    [Fact]
    public async Task FifthPairFailureAndTwentiethIpFailureSetIndependentLimits()
    {
        await using var host = new PergaHostTestCluster();
        await BrowserAuthentication.InitializeOwnerAsync(host.Factory);

        for (var i = 0; i < 5; i++)
        {
            using var failure = await LoginAsync(host.Client, BrowserAuthentication.Username.ToUpperInvariant(), "wrong");
            await AssertErrorAsync(failure, 401, "invalid_credentials");
        }

        using (var pairLimited = await LoginAsync(host.Client))
        {
            await AssertErrorAsync(pairLimited, 429, "login_throttled");
            Assert.InRange(pairLimited.Headers.RetryAfter!.Delta!.Value, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(15));
        }

        for (var i = 0; i < 15; i++)
        {
            using var failure = await LoginAsync(host.Client, $"unknown-{i}", "wrong");
            await AssertErrorAsync(failure, 401, "invalid_credentials");
        }

        using var ipLimited = await LoginAsync(host.Client, "new-username", "wrong");
        await AssertErrorAsync(ipLimited, 429, "login_throttled");
        Assert.InRange(ipLimited.Headers.RetryAfter!.Delta!.Value, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(15));
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    public async Task ExhaustedFailureWindowReopensAutomaticallyAndReportsRetry(int ipLimit, int pairLimit)
    {
        await using var host = new PergaHostTestCluster(configureExtraServices: services =>
            services.Configure<LoginRateLimitOptions>(options =>
            {
                options.IpLimit = ipLimit;
                options.PairLimit = pairLimit;
                options.Window = TimeSpan.FromSeconds(4);
                options.SegmentsPerWindow = 4;
            }));
        await BrowserAuthentication.InitializeOwnerAsync(host.Factory);

        using var failure = await LoginAsync(host.Client, password: "wrong");
        await AssertErrorAsync(failure, 401, "invalid_credentials");
        var username = ipLimit == 1 ? "another-user" : BrowserAuthentication.Username;
        using var limited = await LoginAsync(host.Client, username);
        await AssertErrorAsync(limited, 429, "login_throttled");

        await Task.Delay(TimeSpan.FromMilliseconds(1_600));
        using var later = await LoginAsync(host.Client);
        await AssertErrorAsync(later, 429, "login_throttled");
        Assert.InRange(later.Headers.RetryAfter!.Delta!.Value, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3));

        await AssertLoginEventuallySucceedsAsync(host.Client);
    }

    [Fact]
    public async Task FullPartitionTableRetainsActiveFailuresAndReclaimsExpiredEntries()
    {
        await using var host = new PergaHostTestCluster(configureExtraServices: services =>
            services.Configure<LoginRateLimitOptions>(options =>
            {
                options.PairLimit = 1;
                options.MaxPairPartitions = 2;
                options.Window = TimeSpan.FromSeconds(2);
                options.SegmentsPerWindow = 1;
            }));
        await BrowserAuthentication.InitializeOwnerAsync(host.Factory);

        foreach (var username in new[] { "first-user", "second-user" })
        {
            using var failure = await LoginAsync(host.Client, username, "wrong");
            await AssertErrorAsync(failure, 401, "invalid_credentials");
        }
        using var capacityLimited = await LoginAsync(host.Client);
        await AssertErrorAsync(capacityLimited, 429, "login_throttled");
        using var stillLimited = await LoginAsync(host.Client, "first-user", "wrong");
        await AssertErrorAsync(stillLimited, 429, "login_throttled");

        await AssertLoginEventuallySucceedsAsync(host.Client);
    }

    private static async Task AssertLoginEventuallySucceedsAsync(HttpClient client)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (true)
        {
            using var retry = await LoginAsync(client, ct: deadline.Token);
            if (retry.IsSuccessStatusCode)
                break;
            await AssertErrorAsync(retry, 429, "login_throttled");
            await Task.Delay(100, deadline.Token);
        }
    }

    [Fact]
    public async Task ExhaustedPairIsRejectedBeforeCheckingTheGlobalKdfGate()
    {
        using var hasher = new BlockingHasher(1, PasswordVerificationResult.Failed, blockPassword: "hold");
        await using var host = new PergaHostTestCluster(configureExtraServices: services =>
        {
            services.AddSingleton<IAuthPasswordHasher>(hasher);
            services.Configure<LoginRateLimitOptions>(options =>
            {
                options.PairLimit = 1;
                options.KdfConcurrency = 1;
                options.KdfQueueLimit = 0;
            });
        });
        await BrowserAuthentication.InitializeOwnerAsync(host.Factory);

        using var failure = await LoginAsync(host.Client, password: "wrong");
        await AssertErrorAsync(failure, 401, "invalid_credentials");
        var waiting = LoginAsync(host.Client, "different-user", "hold");
        try
        {
            await hasher.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
            using var limited = await LoginAsync(host.Client);
            await AssertErrorAsync(limited, 429, "login_throttled");
            Assert.True(limited.Headers.RetryAfter?.Delta > TimeSpan.FromSeconds(2));
        }
        finally
        {
            hasher.Release.Set();
            using var response = await waiting;
        }
    }

    [Fact]
    public async Task SuccessfulLoginsDoNotConsumeFailurePermits()
    {
        await using var host = new PergaHostTestCluster(configureExtraServices: services =>
            services.Configure<LoginRateLimitOptions>(options => options.PairLimit = 2));
        await BrowserAuthentication.InitializeOwnerAsync(host.Factory);
        for (var i = 0; i < 6; i++)
        {
            using var success = await LoginAsync(host.Client);
            Assert.True(success.IsSuccessStatusCode, await success.Content.ReadAsStringAsync());
        }
        for (var i = 0; i < 2; i++)
        {
            using var failure = await LoginAsync(host.Client, password: "wrong");
            await AssertErrorAsync(failure, 401, "invalid_credentials");
        }
        using var limited = await LoginAsync(host.Client);
        await AssertErrorAsync(limited, 429, "login_throttled");
    }

    [Fact]
    public async Task ConcurrentAttemptsRespectKdfSlotsAndPairFailureCap()
    {
        using var hasher = new BlockingHasher(signalAfter: 4, PasswordVerificationResult.Failed);
        await using var host = new PergaHostTestCluster(configureExtraServices: services =>
        {
            services.AddSingleton<IAuthPasswordHasher>(hasher);
            services.Configure<LoginRateLimitOptions>(options => options.KdfWaitTimeout = TimeSpan.FromSeconds(10));
        });
        await BrowserAuthentication.InitializeOwnerAsync(host.Factory);

        var requests = Enumerable.Range(0, 8).Select(_ => LoginAsync(host.Client, password: "wrong")).ToArray();
        try
        {
            await hasher.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Equal(4, hasher.Calls);
        }
        finally
        {
            hasher.Release.Set();
        }

        var responses = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(15));
        try
        {
            Assert.Equal(5, responses.Count(response => (int)response.StatusCode == 401));
            Assert.Equal(3, responses.Count(response => (int)response.StatusCode == 429));
            Assert.Equal(5, hasher.Calls);
            Assert.Equal(4, hasher.Peak);
        }
        finally
        {
            foreach (var response in responses)
                response.Dispose();
        }
    }

    [Fact]
    public async Task FullKdfQueueReturnsRetryAfterWithoutAccountingForFailure()
    {
        using var hasher = new BlockingHasher(signalAfter: 1, PasswordVerificationResult.Success);
        await using var host = new PergaHostTestCluster(configureExtraServices: services =>
        {
            services.AddSingleton<IAuthPasswordHasher>(hasher);
            services.Configure<LoginRateLimitOptions>(options =>
            {
                options.KdfConcurrency = 1;
                options.KdfQueueLimit = 0;
                options.PairLimit = 1;
            });
        });
        await BrowserAuthentication.InitializeOwnerAsync(host.Factory);

        var first = LoginAsync(host.Client);
        try
        {
            await hasher.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
            using var limited = await LoginAsync(host.Client, "different-user");
            await AssertErrorAsync(limited, 429, "login_throttled");
            Assert.Equal(TimeSpan.FromSeconds(2), limited.Headers.RetryAfter?.Delta);
        }
        finally
        {
            hasher.Release.Set();
        }

        using var response = await first;
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        using var next = await LoginAsync(host.Client);
        Assert.True(next.IsSuccessStatusCode, await next.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task DisconnectDuringEvenSuccessfulKdfConsumesFailurePermit()
    {
        using var hasher = new BlockingHasher(signalAfter: 1, PasswordVerificationResult.Success);
        var completion = new RequestCompletion();
        var directory = Directory.CreateTempSubdirectory("perga-aborted-login-");
        try
        {
            await using var factory = new IsolatedPergaHostFactory(directory.FullName, configureServices: services =>
            {
                services.AddSingleton<IAuthPasswordHasher>(hasher);
                services.Configure<LoginRateLimitOptions>(options => options.PairLimit = 1);
                services.AddSingleton<IStartupFilter>(completion);
            });
            factory.UseKestrel(0);
            using var client = factory.CreateClient();
            await BrowserAuthentication.InitializeOwnerAsync(factory);

            using var abort = new CancellationTokenSource();
            var login = LoginAsync(client, ct: abort.Token);
            try
            {
                await hasher.Entered.Task.WaitAsync(TimeSpan.FromSeconds(8));
                await abort.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => login.WaitAsync(TimeSpan.FromSeconds(8)));
                await completion.Aborted.Task.WaitAsync(TimeSpan.FromSeconds(8));
            }
            finally
            {
                hasher.Release.Set();
            }

            await completion.Finished.Task.WaitAsync(TimeSpan.FromSeconds(8));
            using var limited = await LoginAsync(client);
            await AssertErrorAsync(limited, 429, "login_throttled");
            Assert.Equal(1, hasher.Calls);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private sealed class BlockingHasher(int signalAfter, PasswordVerificationResult result, string? blockPassword = null)
        : IAuthPasswordHasher, IDisposable
    {
        private int _calls;
        private int _active;
        private int _peak;
        private readonly object _gate = new();

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new();
        public int Calls => Volatile.Read(ref _calls);
        public int Peak => Volatile.Read(ref _peak);
        public string Hash(string password) => "test-hash";
        public PasswordVerificationResult VerifyDummy(string password) => Verify(password, "test-hash");

        public PasswordVerificationResult Verify(string password, string encodedHash)
        {
            if (blockPassword is not null && password != blockPassword)
                return result;
            lock (_gate)
            {
                _peak = Math.Max(_peak, ++_active);
                if (++_calls == signalAfter)
                    Entered.TrySetResult();
            }
            try
            {
                if (!Release.Wait(TimeSpan.FromSeconds(12)))
                    throw new TimeoutException("Test did not release the KDF.");
                return result;
            }
            finally
            {
                lock (_gate)
                    _active--;
            }
        }

        public void Dispose() => Release.Dispose();
    }

    private sealed class RequestCompletion : IStartupFilter
    {
        public TaskCompletionSource Aborted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, proceed) =>
            {
                using var aborted = context.RequestAborted.Register(() => Aborted.TrySetResult());
                try
                {
                    await proceed(context);
                }
                finally
                {
                    if (context.Request.Path == "/api/v1/auth/login")
                        Finished.TrySetResult();
                }
            });
            next(app);
        };
    }
}
