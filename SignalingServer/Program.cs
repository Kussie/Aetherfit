using AetherfitSignaling;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.UseWebSockets();

var sessions = new SessionStore();
_ = sessions.RunSweepLoopAsync(app.Lifetime.ApplicationStopping);

app.MapGet("/", () => "Aetherfit signaling server is running.");

app.Map("/signal", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    await SignalConnectionHandler.HandleAsync(socket, sessions, context.RequestAborted);
});

app.Run();
