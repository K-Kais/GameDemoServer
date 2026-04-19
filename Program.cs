using GameDemoServer.Cores;
using GameDemoServer.Options;
using GameDemoServer.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<JwtSettings>(builder.Configuration.GetSection(JwtSettings.SectionName));
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<GameManager>();
builder.Services.AddHostedService<InputSyncBroadcastService>();

builder.Services.AddControllers();
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});
app.UseMiddleware<WebSocketHandler>();

app.MapControllers();

app.Run();
