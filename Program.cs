using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Types;
using Microsoft.Data.SqlClient;
using System.Net.Http;
using Telegram.Bot.Polling;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using System;
using Telegram.Bot.Types.Enums;

class Program
{
    private static string telegramToken = "7799080092:AAE5BB_IVmMtw7zgcY_2Z_G2aAciLYR5_pU";
    private static string _apiKey = "AIzaSyDNkd2CaRjaGyZ_HsQblzwACuTuq6os_HU";
    private static string dbConnectionString = "DATA SOURCE=DESKTOP-2S6R7FK\\SQLEXPRESS;INITIAL CATALOG=FinanceDB;USER=sa;PASSWORD=SatrackAL24*;MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=True";

    private static TelegramBotClient bot;
    private static GeminiService geminiService;

    static async Task Main()
    {
        bot = new TelegramBotClient(telegramToken);
        geminiService = new GeminiService(_apiKey);

        // Inicializar DB
        using (var conn = new SqlConnection(dbConnectionString))
        {
            await conn.OpenAsync();
        }

        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = Array.Empty<UpdateType>()
        };

        bot.StartReceiving(
            updateHandler: HandleUpdateAsync,
            pollingErrorHandler: HandleErrorAsync,
            receiverOptions: receiverOptions
        );

        var me = await bot.GetMeAsync();
        Console.WriteLine($"🤖 Bot {me.Username} iniciado. Presiona Ctrl+C para salir.");
        await Task.Delay(-1);
    }

    static Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken token)
    {
        Console.WriteLine($"❌ Error: {exception.Message}");
        return Task.CompletedTask;
    }

    static async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken token)
    {
        if (update.Message is not { Text: { } messageText }) return;

        var chatId = update.Message.Chat.Id;
        var user = update.Message.Chat.FirstName ?? "Usuario";

        GuardarMensaje(user, messageText);
        var contexto = ObtenerContexto(user);

        // Obtener respuesta de Gemini
        var response = await geminiService.ConsultarGemini(contexto, messageText);

        await botClient.SendTextMessageAsync(chatId, response, cancellationToken: token);
    }

    static void GuardarMensaje(string usuario, string texto)
    {
        try
        {
            using var conn = new SqlConnection(dbConnectionString);
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO Mensajes (Usuario, Texto, FechaHora) VALUES (@u, @t, @f)";
            cmd.Parameters.AddWithValue("@u", usuario);
            cmd.Parameters.AddWithValue("@t", texto);
            cmd.Parameters.AddWithValue("@f", DateTime.Now);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error guardando mensaje: {ex.Message}");
        }
    }

    static string ObtenerContexto(string usuario, int limite = 100)
    {
        try
        {
            using var conn = new SqlConnection(dbConnectionString);
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT TOP (@l) Texto, FechaHora FROM Mensajes WHERE Usuario = @u ORDER BY Id DESC";
            cmd.Parameters.AddWithValue("@u", usuario);
            cmd.Parameters.AddWithValue("@l", limite);
            using var reader = cmd.ExecuteReader();

            var mensajes = new List<(string Texto, DateTime FechaHora)>();

            while (reader.Read())
            {
                var texto = reader.GetString(0);         // Columna Texto
                var fecha = reader.GetDateTime(1);       // Columna FechaHora
                mensajes.Insert(0, (texto, fecha));      // Insertar como tupla
            }

            return string.Join("\n", mensajes);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error obteniendo contexto: {ex.Message}");
            return string.Empty;
        }
    }
}

public class GeminiService
{
    private readonly string _apiKey;
    private readonly HttpClient _httpClient;

    public GeminiService(string apiKey)
    {
        _apiKey = apiKey;
        _httpClient = new HttpClient();
    }

    public async Task<string> ConsultarGemini(string mensaje, string nuevoTexto)
    {
        try
        {
            // Construir el prompt completo
            string prompt = $"Contexto previo:\n{mensaje}\n\nNueva entrada:\n{nuevoTexto}\n\nRespuesta:";

            // URL CORRECTA para la API de Gemini
            string url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.0-flash:generateContent?key={_apiKey}";

            // Preparar el payload
            var payload = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new[]
                        {
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

            // Serializar a JSON
            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            // Realizar la petición
            var response = await _httpClient.PostAsync(url, content);
            response.EnsureSuccessStatusCode();

            // Leer y procesar la respuesta
            var responseJson = await response.Content.ReadAsStringAsync();
            return ProcesarRespuestaGemini(responseJson);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private string ProcesarRespuestaGemini(string jsonResponse)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonResponse);
            var root = doc.RootElement;

            // Extraer el texto de la respuesta
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
            Console.WriteLine($"Error procesando respuesta: {ex.Message}");
            return "Error procesando la respuesta JSON";
        }
    }
}

