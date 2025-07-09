using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using IngeTechCRM.Models;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;

namespace IngeTechCRM
{
    public class ComunicadoProgramadoService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<ComunicadoProgramadoService> _logger;
        private readonly TimeSpan _delay = TimeSpan.FromSeconds(30); // Verificar cada 30 segundos para pruebas

        public ComunicadoProgramadoService(
            IServiceProvider serviceProvider,
            ILogger<ComunicadoProgramadoService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Servicio de comunicados programados iniciado");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ProcesarComunicadosProgramados();
                    await Task.Delay(_delay, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error en el servicio de comunicados programados");
                    // Esperar más tiempo si hay error para evitar spam de logs
                    await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
                }
            }

            _logger.LogInformation("Servicio de comunicados programados detenido");
        }

        private async Task ProcesarComunicadosProgramados()
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<IngeTechDbContext>();
            var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();

            try
            {
                var ahora = DateTime.Now;
                var comunicadosPendientes = await context.Comunicados
                    .Where(c => c.FECHA_ENVIO_PROGRAMADO.HasValue &&
                               c.FECHA_ENVIO_PROGRAMADO <= ahora &&
                               !c.FECHA_ENVIO_REAL.HasValue)
                    .ToListAsync();

                if (comunicadosPendientes.Any())
                {
                    _logger.LogInformation($"Encontrados {comunicadosPendientes.Count} comunicados pendientes de procesamiento");

                    foreach (var comunicado in comunicadosPendientes)
                    {
                        try
                        {
                            _logger.LogInformation($"Procesando comunicado {comunicado.ID_COMUNICADO}: {comunicado.TITULO}");

                            // Usar la misma lógica del controlador
                            await EnviarComunicadoComoControlador(comunicado.ID_COMUNICADO, context, configuration);

                            _logger.LogInformation($"Comunicado {comunicado.ID_COMUNICADO} procesado exitosamente");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"Error al procesar comunicado {comunicado.ID_COMUNICADO}");
                        }
                    }
                }
                else
                {
                    _logger.LogDebug("No hay comunicados pendientes de procesamiento");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al buscar comunicados programados");
            }
        }

        // Replicar exactamente la misma lógica del controlador
        private async Task EnviarComunicadoComoControlador(int idComunicado, IngeTechDbContext context, IConfiguration configuration)
        {
            var comunicado = await context.Comunicados
                .Include(c => c.Segmentos)
                .FirstOrDefaultAsync(c => c.ID_COMUNICADO == idComunicado);

            if (comunicado == null)
            {
                return;
            }

            // Si ya fue enviado, no volver a enviar
            if (comunicado.FECHA_ENVIO_REAL.HasValue)
            {
                return;
            }

            // Obtener segmentos
            var segmentos = comunicado.Segmentos.ToList();

            // Buscar usuarios destinatarios según los segmentos
            var usuariosQuery = context.Usuarios.AsQueryable();

            // Si no hay segmentos, enviar a todos los usuarios de tipo cliente
            if (segmentos.Count == 0)
            {
                var tipoClienteId = await context.TiposUsuario
                    .Where(t => t.DESCRIPCION == "Cliente")
                    .Select(t => t.ID_TIPO_USUARIO)
                    .FirstOrDefaultAsync();

                if (tipoClienteId > 0)
                {
                    usuariosQuery = usuariosQuery.Where(u => u.ID_TIPO_USUARIO == tipoClienteId);
                }
            }
            else
            {
                // Filtrar por provincias
                var provinciasIds = segmentos
                    .Where(s => s.ID_PROVINCIA.HasValue)
                    .Select(s => s.ID_PROVINCIA.Value)
                    .Distinct()
                    .ToList();

                // Filtrar por tipos de usuario
                var tiposUsuarioIds = segmentos
                    .Where(s => s.ID_TIPO_USUARIO.HasValue)
                    .Select(s => s.ID_TIPO_USUARIO.Value)
                    .Distinct()
                    .ToList();

                // Construir consulta según los filtros
                if (provinciasIds.Count > 0 && tiposUsuarioIds.Count > 0)
                {
                    usuariosQuery = usuariosQuery.Where(u =>
                        provinciasIds.Contains(u.ID_PROVINCIA) && tiposUsuarioIds.Contains(u.ID_TIPO_USUARIO));
                }
                else if (provinciasIds.Count > 0)
                {
                    usuariosQuery = usuariosQuery.Where(u => provinciasIds.Contains(u.ID_PROVINCIA));
                }
                else if (tiposUsuarioIds.Count > 0)
                {
                    usuariosQuery = usuariosQuery.Where(u => tiposUsuarioIds.Contains(u.ID_TIPO_USUARIO));
                }
            }

            // Obtener usuarios destinatarios
            var usuarios = await usuariosQuery.ToListAsync();

            _logger.LogInformation($"Enviando comunicado {idComunicado} a {usuarios.Count} usuarios");

            // Crear registros de envío
            foreach (var usuario in usuarios)
            {
                var envioExistente = await context.EnviosComunicado
                    .AnyAsync(e => e.ID_COMUNICADO == idComunicado && e.ID_USUARIO_DESTINATARIO == usuario.IDENTIFICACION);

                if (!envioExistente)
                {
                    var envio = new EnvioComunicado
                    {
                        ID_COMUNICADO = idComunicado,
                        ID_USUARIO_DESTINATARIO = usuario.IDENTIFICACION,
                        FECHA_ENVIO = DateTime.Now
                    };

                    context.EnviosComunicado.Add(envio);

                    // Enviar por correo si está habilitado y el usuario tiene email
                    if (!string.IsNullOrEmpty(usuario.CORREO_ELECTRONICO))
                    {
                        await EnviarPorCorreo(usuario.CORREO_ELECTRONICO, comunicado.TITULO, comunicado.MENSAJE, configuration);
                    }

                    // Enviar por WhatsApp si está habilitado y el usuario tiene teléfono
                    if (!string.IsNullOrEmpty(usuario.TELEFONO))
                    {
                        await EnviarPorWhatsApp(usuario.TELEFONO, comunicado.TITULO, comunicado.MENSAJE, configuration);
                    }
                }
            }

            // Actualizar fecha de envío real
            comunicado.FECHA_ENVIO_REAL = DateTime.Now;
            context.Update(comunicado);

            await context.SaveChangesAsync();

            _logger.LogInformation($"Comunicado {idComunicado} marcado como enviado");
        }

        private async Task EnviarPorCorreo(string email, string titulo, string mensaje, IConfiguration configuration)
        {
            try
            {
                using (var client = new System.Net.Mail.SmtpClient(configuration["Email:SmtpServer"]))
                {
                    client.Port = int.Parse(configuration["Email:Port"]);
                    client.Credentials = new System.Net.NetworkCredential(
                        configuration["Email:Username"],
                        configuration["Email:Password"]);
                    client.EnableSsl = true;

                    var mailMessage = new System.Net.Mail.MailMessage
                    {
                        From = new System.Net.Mail.MailAddress(configuration["Email:FromAddress"]),
                        Subject = titulo,
                        Body = mensaje,
                        IsBodyHtml = true
                    };

                    mailMessage.To.Add(email);
                    await client.SendMailAsync(mailMessage);

                    _logger.LogInformation($"Email enviado exitosamente a {email}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error al enviar correo a {email}");
            }
        }

        private async Task EnviarPorWhatsApp(string telefono, string titulo, string mensaje, IConfiguration configuration)
        {
            try
            {
                // Limpiar y formatear el número de teléfono
                telefono = new string(telefono.Where(c => char.IsDigit(c)).ToArray());

                if (!telefono.StartsWith("506") && telefono.Length == 8)
                {
                    telefono = "506" + telefono;
                }

                // Crear el mensaje formateado para WhatsApp
                string mensajeWhatsApp = $"*{titulo}*\n\n{mensaje}";

                // Obtener configuración de la API de WhatsApp
                string apiUrl = configuration["WhatsAppSettings:ApiUrl"];
                string phoneNumberId = configuration["WhatsAppSettings:PhoneNumberId"];
                string accessToken = configuration["WhatsAppSettings:AccessToken"];

                // En un entorno de desarrollo/prueba, simular el envío
                if (string.IsNullOrEmpty(apiUrl) || string.IsNullOrEmpty(phoneNumberId) || string.IsNullOrEmpty(accessToken))
                {
                    _logger.LogInformation($"WhatsApp simulado enviado a {telefono} (configuración no disponible)");
                    return;
                }

                // Construir la URL completa
                string url = $"{apiUrl}{phoneNumberId}/messages";

                var requestData = new
                {
                    messaging_product = "whatsapp",
                    recipient_type = "individual",
                    to = telefono,
                    type = "text",
                    text = new { body = mensajeWhatsApp }
                };

                string jsonContent = System.Text.Json.JsonSerializer.Serialize(requestData);
                var content = new StringContent(jsonContent, System.Text.Encoding.UTF8, "application/json");

                var httpClientFactory = _serviceProvider.GetRequiredService<IHttpClientFactory>();
                using (var httpClient = httpClientFactory.CreateClient())
                {
                    httpClient.DefaultRequestHeaders.Clear();
                    httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");

                    var response = await httpClient.PostAsync(url, content);

                    if (response.IsSuccessStatusCode)
                    {
                        _logger.LogInformation($"WhatsApp enviado exitosamente a {telefono}");
                    }
                    else
                    {
                        string errorResponse = await response.Content.ReadAsStringAsync();
                        _logger.LogWarning($"Error al enviar WhatsApp a {telefono}: {response.StatusCode} - {errorResponse}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error al enviar WhatsApp a {telefono}");
            }
        }
    }
}