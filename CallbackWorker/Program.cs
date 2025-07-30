using Schedule;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<CallbackWorker>();

var host = builder.Build();
host.Run();
