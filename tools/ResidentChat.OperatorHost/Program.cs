using Npgsql;
using ResidentChat.Api;

// RC-29 explicit owner-local READ-ONLY operator preview host.
// No Docker/secret env vars, no Telegram intake, no polling and no public
// network bind. All credentials are read from separately mounted local files.
// This is an isolated fixture-tenant preview, not resident production access.
const string Tenant = "27272727-2727-4727-8727-272727272727";
const string Issuer = "residentchat.rc29.local-operator";
const string Audience = "residentchat.rc29.owner-preview";
const string SecretDir = "/run/rc29-secrets";

try
{
    if (args.Length != 1 || args[0] != "--serve-owner-readonly")
        throw new InvalidOperationException("RC29 mode not explicitly selected.");

    string ReadLocalSecret(string name, bool hex = true)
    {
        var file = Path.Combine(SecretDir, name);
        if (!File.Exists(file))
            throw new InvalidOperationException("RC29 local file missing.");
        string value = File.ReadAllText(file).Trim();
        if (value.Length < 64 || value.Length > 512
            || (hex && !value.All(Uri.IsHexDigit)))
            throw new InvalidOperationException("RC29 local secret format invalid.");
        return value;
    }

    string pgPassword = ReadLocalSecret("pg-runtime-password");
    string signing = ReadLocalSecret("rc28-operator-jwt");
    string audit = ReadLocalSecret("rc28-operator-audit");
    _ = ReadLocalSecret("rc28-dp-password");
    if (signing == audit || pgPassword == signing || pgPassword == audit)
        throw new InvalidOperationException("RC29 keys must be distinct.");

    string ring = "/run/rc29-data-protection/keys";
    string cert = Path.Combine(SecretDir, "rc28-dp-certificate.pfx");
    string certPassword = Path.Combine(SecretDir, "rc28-dp-password");
    if (!Directory.Exists(ring) || !File.Exists(cert))
        throw new InvalidOperationException("RC29 persistent keyring unavailable.");

    var db = new NpgsqlConnectionStringBuilder {
        Host = "/var/run/postgresql",
        Database = "residentchat",
        Username = "residentchat_lab_runtime",
        Password = pgPassword,
        SslMode = SslMode.Disable,
        Pooling = true,
        MaximumPoolSize = 5,
        Port = 5432,
        Timeout = 5,
        CommandTimeout = 10,
        ApplicationName = "residentchat-rc29-local-viewer"
    }.ConnectionString;

    var configuration = new Dictionary<string, string?> {
        ["ResidentChat:Operator:Enabled"] = "true",
        ["ResidentChat:Operator:Issuer"] = Issuer,
        ["ResidentChat:Operator:Audience"] = Audience,
        ["ResidentChat:Operator:JwtSigningKey"] = signing,
        ["ResidentChat:Operator:AuditHmacKey"] = audit,
        ["ResidentChat:Operator:ConnectionString"] = db,
        ["ResidentChat:Operator:AllowedTenantIds:0"] = Tenant,
        ["ResidentChat:OperatorConsole:Enabled"] = "true",
        ["ResidentChat:OperatorConsole:Mode"] = "persistent-lab-readonly",
        ["ResidentChat:PersistentReadOnly:Enabled"] = "true",
        ["ResidentChat:PersistentDataProtection:Enabled"] = "true",
        ["ResidentChat:PersistentDataProtection:KeyDirectory"] = ring,
        ["ResidentChat:PersistentDataProtection:CertificatePath"] = cert,
        ["ResidentChat:PersistentDataProtection:PasswordFile"] = certPassword,
        ["ResidentChat:Telegram:Enabled"] = "false",
        ["ResidentChat:TelegramPolling:Enabled"] = "false",
        ["ResidentChat:DurableIntake:Enabled"] = "false",
        ["ResidentChat:DurableProposals:Enabled"] = "false",
        ["ResidentChat:ClassifiedTelegram:Enabled"] = "false",
        ["ResidentChat:TelegramReview:Enabled"] = "false",
        ["Logging:LogLevel:Default"] = "Warning",
        ["Logging:LogLevel:Microsoft"] = "Warning"
    };

    // Kestrel 127.0.0.1 is non-negotiable. WSL/Windows loopback forwarder
    // must be evaluated by the owner before any always-on deployment.
    await using var app = ResidentChatApp.Build([], configuration);
    app.Urls.Add("http://127.0.0.1:18762");
    await app.RunAsync();
    return 0;
}
catch
{
    // Never print Npgsql/PKCS12 exceptions, secret path contents or JWT.
    Console.Error.WriteLine("RC29_LOCAL_PREVIEW_START_FAIL_SAFE");
    return 1;
}
