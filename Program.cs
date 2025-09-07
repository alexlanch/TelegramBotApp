using Microsoft.Data.SqlClient;
using Telegram.Bot;
using Telegram.Bot.Types;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using System.Threading;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text;
using System.Net.Http;
using Microsoft.AspNetCore.Http;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// --- Configurar servicios ---
// lee variables desde appsettings o variables de entorno (Azure App Settings)
var telegramToken = config["TelegramToken"];
var geminiApiKey = config["GeminiApiKey"];
var connectionString = config.GetConnectionString("DefaultConnection") ?? config["ConnectionStrings:DefaultConnection"];

// valida que existan (en desarrollo puedes lanzar excepción si faltan)
if (string.IsNullOrEmpty(telegramToken))
    throw new InvalidOperationException("Falta TELEGRAM token en configuración (TelegramToken).");
if (string.IsNullOrEmpty(connectionString))
    throw new InvalidOperationException("Falta cadena de conexión en configuración (ConnectionStrings:DefaultConnection).");

// Registrar cliente Telegram
builder.Services.AddSingleton<ITelegramBotClient>(sp => new TelegramBotClient(telegramToken));

// Registrar GeminiService como typed client (inyecta HttpClient y la apiKey)
builder.Services.AddHttpClient<GeminiService>()
    .ConfigureHttpClient(client => {
        // opcional: timeout o headers globales
        client.Timeout = TimeSpan.FromSeconds(60);
    });
builder.Services.AddSingleton(sp =>
{
    var http = sp.GetRequiredService<HttpClient>();
    return new GeminiService(geminiApiKey, http);
});

var app = builder.Build();

// --- Setear webhook automáticamente si APP_BASE_URL está configurada ---
var appBaseUrl = config["AppBaseUrl"]; // ej: https://miapp.azurewebsites.net
if (!string.IsNullOrEmpty(appBaseUrl))
{
    var botClient = app.Services.GetRequiredService<ITelegramBotClient>();
    var webhookUrl = $"{appBaseUrl.TrimEnd('/')}/bot/update";
    Console.WriteLine($"Setting Telegram webhook -> {webhookUrl}");
    // setear webhook (await dentro de inicio)
    await botClient.SetWebhookAsync(webhookUrl);
    Console.WriteLine("Webhook registrado.");
}

// Endpoint que recibirá updates de Telegram
app.MapPost("/bot/update", async (Update update,
                                  ITelegramBotClient botClient,
                                  GeminiService geminiService,
                                  CancellationToken cancellationToken) =>
{
    try
    {
        if (update?.Message?.Text == null)
            return Results.Ok(); // nada que hacer

        var messageText = update.Message.Text;
        var chatId = update.Message.Chat.Id;
        var user = update.Message.Chat.Username ?? update.Message.Chat.FirstName ?? update.Message.Chat.Id.ToString();

        // Guardar mensaje (async)
        await GuardarMensajeAsync(connectionString, user, messageText);

        // Obtener contexto (los últimos N mensajes formateados)
        var contexto = await ObtenerContextoAsync(connectionString, user, limite: 50);

        // Llamar a Gemini (o tu servicio de IA)
        var respuesta = await geminiService.ConsultarGemini(contexto, messageText);

        // Responder por Telegram
        await botClient.SendTextMessageAsync(chatId, respuesta, cancellationToken: cancellationToken);

        return Results.Ok();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error procesando update: {ex.Message}");
        return Results.Ok(); // siempre devolver 200 a Telegram para no reintentar en exceso
    }
});

app.Run();


// ----------------- Helpers DB (async) -----------------
static async Task GuardarMensajeAsync(string connectionString, string usuario, string texto)
{
    try
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO Mensajes (Usuario, Texto, FechaHora) VALUES (@u, @t, @f)";
        cmd.Parameters.AddWithValue("@u", usuario);
        cmd.Parameters.AddWithValue("@t", texto);
        cmd.Parameters.AddWithValue("@f", DateTime.UtcNow); // usa UTC para consistencia
        await cmd.ExecuteNonQueryAsync();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error guardando mensaje: {ex.Message}");
    }
}

static async Task<string> ObtenerContextoAsync(string connectionString, string usuario, int limite = 10)
{
    try
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT TOP (@l) Texto, FechaHora FROM Mensajes WHERE Usuario = @u ORDER BY Id DESC";
        cmd.Parameters.AddWithValue("@u", usuario);
        cmd.Parameters.AddWithValue("@l", limite);

        var mensajes = new List<(string Texto, DateTime FechaHora)>();

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var texto = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            var fecha = reader.IsDBNull(1) ? DateTime.MinValue : reader.GetDateTime(1);
            mensajes.Insert(0, (texto, fecha)); // insertamos al inicio para invertir el ORDER BY DESC
        }

        // Formatear la lista: "YYYY-MM-DD HH:mm - Texto"
        var lines = mensajes.Select(m => $"{m.FechaHora:yyyy-MM-dd HH:mm} - {m.Texto}");
        return string.Join("\n", lines);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error obteniendo contexto: {ex.Message}");
        return string.Empty;
    }
}

public class GeminiService
{
    private readonly string _apiKey;
    private readonly HttpClient _httpClient;

    public GeminiService(string apiKey, HttpClient httpClient)
    {
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<string> ConsultarGemini(string contexto, string nuevoTexto)
    {
        try
        {
            string prompt = $"Contexto previo:\n{contexto}\n\nNueva entrada:\n{nuevoTexto}\n\nRespuesta:";

            string url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.0-flash:generateContent?key={_apiKey}";

            var payload = new
            {
                contents = new[] {
                    new {
                        parts = new[] {
                            new { text = prompt }
                        }
                    }
                },
                generationConfig = new
                {
                    temperature = 0.7,
                    maxOutputTokens = 1024,
                    topP = 0.8,
                    topK = 40
                }
            };

            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(url, content);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync();
            return ProcesarRespuestaGemini(responseJson);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error en GeminiService: {ex.Message}");
            return "Lo siento, ocurrió un error al procesar la petición.";
        }
    }

    private string ProcesarRespuestaGemini(string jsonResponse)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonResponse);
            var root = doc.RootElement;

            // Ajusta según la estructura real que recibas
            var text = root
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString();

            return text ?? "No se recibió respuesta";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error procesando respuesta Gemini: {ex.Message}");
            return "Error procesando la respuesta JSON";
        }
    }
}
