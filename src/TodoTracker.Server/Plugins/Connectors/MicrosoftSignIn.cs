using Microsoft.Identity.Client;

namespace TodoTracker.Server.Plugins.Connectors;

public sealed record MicrosoftSignInView(string State, string? Account = null, string? UserCode = null, string? VerificationUri = null, string? Message = null);

/// <summary>Signing in to Microsoft (for To Do): the device-code way, with the token kept encrypted on this computer.</summary>
public interface IMicrosoftSignIn
{
    MicrosoftSignInView View { get; }

    bool SignedIn { get; }

    /// <summary>Starts signing in: the code to enter at the address shown (finishes on its own once entered).</summary>
    Task<MicrosoftSignInView> StartAsync(string clientId, CancellationToken cancellationToken);

    Task<string> TokenAsync(CancellationToken cancellationToken);

    void SignOut();
}

/// <summary>Sign-in states (the panel reads these names).</summary>
public static class SignInStates
{
    public const string SignedOut = "signedOut";
    public const string Waiting = "waiting";
    public const string SignedIn = "signedIn";
    public const string Failed = "failed";
}

/// <summary>
/// Microsoft sign-in with MSAL: work, school or personal accounts ("common"), the To Do permission only
/// (Tasks.ReadWrite), and MSAL's token cache stored encrypted in the connector's folder. Only the latest sign-in
/// counts: starting another (or signing out) cancels the one before, and a late answer from it is ignored.
/// </summary>
public sealed class MsalSignIn(ConnectorFiles files) : IMicrosoftSignIn
{
    private static readonly string[] Scopes = ["Tasks.ReadWrite"];
    private readonly Lock _lock = new();
    private IPublicClientApplication? _app;
    private string? _clientId;
    private MicrosoftSignInView? _view;
    private CancellationTokenSource? _pending;
    private int _attempt;

    public MicrosoftSignInView View
    {
        get
        {
            lock (_lock)
            {
                return _view ??= File.Exists(files.SecretPath) ? new MicrosoftSignInView(SignInStates.SignedIn) : new MicrosoftSignInView(SignInStates.SignedOut);
            }
        }
    }

    /// <summary>Signed in when there's a token cache (a sign-in still waiting or one that failed doesn't change that).</summary>
    public bool SignedIn => File.Exists(files.SecretPath) && View.State != SignInStates.SignedOut;

    public async Task<MicrosoftSignInView> StartAsync(string clientId, CancellationToken cancellationToken)
    {
        var app = App(clientId);
        CancellationTokenSource cancel;
        int attempt;
        lock (_lock)
        {
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = cancel = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            attempt = ++_attempt;
        }

        var shown = new TaskCompletionSource<MicrosoftSignInView>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await app.AcquireTokenWithDeviceCode(Scopes, code =>
                {
                    var waiting = new MicrosoftSignInView(SignInStates.Waiting, UserCode: code.UserCode, VerificationUri: code.VerificationUrl, Message: code.Message);
                    Set(waiting, attempt);
                    shown.TrySetResult(waiting);
                    return Task.CompletedTask;
                }).ExecuteAsync(cancel.Token).ConfigureAwait(false);
                Set(new MicrosoftSignInView(SignInStates.SignedIn, result.Account?.Username), attempt);
            }
            catch (Exception ex) when (ex is MsalException or HttpRequestException or OperationCanceledException)
            {
                var failed = new MicrosoftSignInView(SignedIn ? SignInStates.SignedIn : SignInStates.Failed, Message: ex is OperationCanceledException ? "Sign-in stopped." : ex.Message);
                Set(failed, attempt);
                shown.TrySetResult(failed);
            }
        }, CancellationToken.None);
        try
        {
            return await shown.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException("Microsoft didn't answer. Check the connection and the client id, then try again.");
        }
    }

    public async Task<string> TokenAsync(CancellationToken cancellationToken)
    {
        var clientId = files.Settings.ClientId ?? throw new InvalidOperationException("Add the app registration's client id first.");
        var app = App(clientId);
        var account = (await app.GetAccountsAsync().ConfigureAwait(false)).FirstOrDefault()
            ?? throw new InvalidOperationException("Sign in to Microsoft first.");
        try
        {
            var result = await app.AcquireTokenSilent(Scopes, account).ExecuteAsync(cancellationToken).ConfigureAwait(false);
            return result.AccessToken;
        }
        catch (MsalUiRequiredException)
        {
            Set(new MicrosoftSignInView(SignInStates.SignedOut, Message: "Sign in to Microsoft again."), null);
            throw new InvalidOperationException("Sign in to Microsoft again.");
        }
        catch (MsalException ex)
        {
            // Offline after sleep, or Microsoft busy: the sync says so and tries again later.
            throw new InvalidOperationException($"Couldn't reach Microsoft sign-in: {ex.Message}", ex);
        }
    }

    public void SignOut()
    {
        lock (_lock)
        {
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = null;
            _attempt++;
            if (File.Exists(files.SecretPath))
            {
                File.Delete(files.SecretPath);
            }

            _app = null;
            _view = new MicrosoftSignInView(SignInStates.SignedOut);
        }
    }

    /// <summary>Records what a sign-in came to, unless a newer one started since (null: whatever is current).</summary>
    private void Set(MicrosoftSignInView view, int? attempt)
    {
        lock (_lock)
        {
            if (attempt is null || attempt == _attempt)
            {
                _view = view;
            }
        }
    }

    private IPublicClientApplication App(string clientId)
    {
        lock (_lock)
        {
            if (_app is not null && _clientId == clientId)
            {
                return _app;
            }

            _clientId = clientId;
            _app = PublicClientApplicationBuilder.Create(clientId)
                .WithAuthority("https://login.microsoftonline.com/common")
                .WithDefaultRedirectUri()
                .Build();
            _app.UserTokenCache.SetBeforeAccess(args =>
            {
                if (SecretFile.Read(files.SecretPath) is { } cache)
                {
                    args.TokenCache.DeserializeMsalV3(Convert.FromBase64String(cache));
                }
            });
            _app.UserTokenCache.SetAfterAccess(args =>
            {
                if (args.HasStateChanged)
                {
                    files.SaveSecret(Convert.ToBase64String(args.TokenCache.SerializeMsalV3()));
                }
            });
            return _app;
        }
    }
}
