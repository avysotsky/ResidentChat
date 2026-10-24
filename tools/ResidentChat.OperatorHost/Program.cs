using Npgsql;
using ResidentChat.Api;

// Temporary RC29 compiler isolation. Do not deploy this probe.
try
{
    if (args.Length != 1 || args[0] != "--rc29-compile-probe")
        return 2;
    var connection = new NpgsqlConnectionStringBuilder {
        Host = "/var/run/postgresql",
        Database = "residentchat",
        Username = "residentchat_lab_runtime",
        Password = "synthetic-compiler-only",
        SslMode = SslMode.Disable,
        Pooling = true,
        MaximumPoolSize = 5,
        Port = 5432,
        Timeout = 5,
        CommandTimeout = 10,
        ApplicationName = "residentchat-rc29-local-viewer"
    }.ConnectionString;
    var config = new Dictionary<string,string?> {
        ["ResidentChat:Operator:Enabled"] = "false",
        ["ResidentChat:Telegram:Enabled"] = "false",
        ["ResidentChat:Operator:ConnectionString"] = connection
    };
    await using var app = ResidentChatApp.Build([], config);
    app.Urls.Add("http://127.0.0.1:18762");
    await app.RunAsync();
    return 0;
}
catch { return 1; }
