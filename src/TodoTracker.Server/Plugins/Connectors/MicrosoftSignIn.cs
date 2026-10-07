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

/// <summary>
/// Microsoft sign-in with MSAL: work, school or personal accounts ("common"), the To Do permission only
/// (Tasks.ReadWrite), and MSAL's token cache stored encrypted in the connector's folder.
/// </summary>
public sealed class MsalSignIn(ConnectorFiles files) : IMicrosoftSignIn
{
    private static readonly string[] Scopes = ["Tasks.ReadWrite"];
    private readonly Lock _lock = new();
    private IPublicClientApplication? _app;
    private string? _clientId;
    private MicrosoftSignInView? _view;

    public MicrosoftSignInView View
    {
        get
        {
            lock (_lock)
            {
                return _view ??= File.Exists(files.SecretPath) ? new MicrosoftSignInView("signedIn") : new MicrosoftSignInView("signedOut");
            }
        }
    }

    public bool SignedIn => View.State == "signedIn";

    public async Task<MicrosoftSignInView> StartAsync(string clientId, CancellationToken cancellationToken)
    {
        var app = App(clientId);
        var shown = new TaskCompletionSource<MicrosoftSignInView>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await app.AcquireTokenWithDeviceCode(Scopes, code =>
                {
                    var waiting = new MicrosoftSignInView("waiting", UserCode: code.UserCode, VerificationUri: code.VerificationUrl, Message: code.Message);
                    Set(waiting);
                    shown.TrySetResult(waiting);
                    return Task.CompletedTask;
                }).ExecuteAsync(CancellationToken.None).ConfigureAwait(false);
                Set(new MicrosoftSignInView("signedIn", result.Account?.Username));
            }
            catch (Exception ex) when (ex is MsalException or HttpRequestException or OperationCanceledException)
            {
                var failed = new MicrosoftSignInView("failed", Message: ex.Message);
                Set(failed);
                shown.TrySetResult(failed);
            }
        }, CancellationToken.None);
        return await shown.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
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
            Set(new MicrosoftSignInView("signedIn", result.Account?.Username));
            return result.AccessToken;
        }
        catch (MsalUiRequiredException)
        {
            Set(new MicrosoftSignInView("signedOut", Message: "Sign in to Microsoft again."));
            throw new InvalidOperationException("Sign in to Microsoft again.");
        }
    }

    public void SignOut()
    {
        lock (_lock)
        {
            if (File.Exists(files.SecretPath))
            {
                File.Delete(files.SecretPath);
            }

            _app = null;
            _view = new MicrosoftSignInView("signedOut");
        }
    }

    private void Set(MicrosoftSignInView view)
    {
        lock (_lock)
        {
            _view = view;
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
