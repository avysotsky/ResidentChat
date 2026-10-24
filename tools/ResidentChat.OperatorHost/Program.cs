using ResidentChat.Api;

// RC-29 compiler-isolation probe: not a deployable host.
try
{
    if (args.Length != 1 || args[0] != "--rc29-compile-probe")
        return 2;
    await using var app = ResidentChatApp.Build([], new Dictionary<string, string?>
    {
        ["ResidentChat:Telegram:Enabled"] = "false",
        ["ResidentChat:Operator:Enabled"] = "false"
    });
    app.Urls.Add("http://127.0.0.1:18762");
    await app.RunAsync();
    return 0;
}
catch
{
    return 1;
}
