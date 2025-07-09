﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Net.Http;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using IngeTechCRM.Models;

namespace IngeTechCRM.Controllers
{
    public class ComunicadoController : Controller
    {
        private readonly IngeTechDbContext _context;
        private readonly IConfiguration _configuration;

        public ComunicadoController(IngeTechDbContext context, IConfiguration configuration)
        {
            _context = context;
            _configuration = configuration;
        }

        public IActionResult Index(string filtro = "todos", bool autorefresh = false)
        {
            // Verificar si el usuario es administrador
            var tipoUsuarioId = HttpContext.Session.GetInt32("TipoUsuarioId");
            if (tipoUsuarioId != 1)
            {
                return RedirectToAction("Index", "Home");
            }

            // Forzar nueva consulta desde base de datos
            _context.ChangeTracker.Clear();

            var comunicados = _context.Comunicados
                .Include(c => c.UsuarioCreador)
                .OrderByDescending(c => c.FECHA_CREACION)
                .ToList();

            ViewBag.Filtro = filtro;
            ViewBag.AutoRefresh = autorefresh;
            ViewBag.UltimaActualizacion = DateTime.Now;

            // Si hay comunicados que acabán de ser enviados, activar auto-refresh
            var recienEnviados = comunicados.Where(c =>
                c.FECHA_ENVIO_REAL.HasValue &&
                c.FECHA_ENVIO_REAL.Value > DateTime.Now.AddMinutes(-2)
            ).Any();

            if (recienEnviados || autorefresh)
            {
                ViewBag.AutoRefresh = true;
            }

            return View(comunicados);
        }

        [Authorize]
        public IActionResult Detalles(int id)
        {
            // Verificar si el usuario es administrador
            var tipoUsuarioId = HttpContext.Session.GetInt32("TipoUsuarioId");
            if (tipoUsuarioId != 1)
            {
                return RedirectToAction("Index", "Home");
            }

            var comunicado = _context.Comunicados
                .Include(c => c.UsuarioCreador)
                .Include(c => c.Segmentos)
                .ThenInclude(s => s.Provincia)
                .Include(c => c.Segmentos)
                .ThenInclude(s => s.TipoUsuario)
                .FirstOrDefault(c => c.ID_COMUNICADO == id);

            if (comunicado == null)
            {
                return NotFound();
            }

            // Obtener información de envíos
            var envios = _context.EnviosComunicado
                .Include(e => e.UsuarioDestinatario)
                .Where(e => e.ID_COMUNICADO == id)
                .ToList();

            ViewBag.TotalEnvios = envios.Count;
            ViewBag.Envios = envios;

            return View(comunicado);
        }

        [Authorize]
        public IActionResult Crear()
        {
            // Verificar si el usuario es administrador
            var tipoUsuarioId = HttpContext.Session.GetInt32("TipoUsuarioId");
            if (tipoUsuarioId != 1)
            {
                return RedirectToAction("Index", "Home");
            }

            ViewBag.Provincias = _context.Provincias.ToList();
            ViewBag.TiposUsuario = _context.TiposUsuario.ToList();

            return View();
        }

        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Crear(Comunicado comunicado, List<int> provinciasSeleccionadas, List<int> tiposUsuarioSeleccionados, bool enviarInmediatamente, bool enviarPorCorreo, bool enviarPorWhatsApp)
        {
            // Verificar si el usuario es administrador
            var tipoUsuarioId = HttpContext.Session.GetInt32("TipoUsuarioId");
            if (tipoUsuarioId != 1)
            {
                return RedirectToAction("Index", "Home");
            }

            ModelState.Remove("Envios");
            ModelState.Remove("Segmentos");
            ModelState.Remove("UsuarioCreador");

            // Validación de fecha de envío programado
            if (comunicado.FECHA_ENVIO_PROGRAMADO.HasValue && !enviarInmediatamente)
            {
                // Permitir fechas futuras (al menos 1 minuto en el futuro)
                var fechaMinima = DateTime.Now.AddMinutes(1);
                if (comunicado.FECHA_ENVIO_PROGRAMADO.Value <= fechaMinima)
                {
                    ModelState.AddModelError("FECHA_ENVIO_PROGRAMADO", "La fecha de envío programado debe ser al menos 1 minuto en el futuro.");
                }
            }

            if (ModelState.IsValid)
            {
                var usuarioId = HttpContext.Session.GetInt32("UsuarioId").Value;
                comunicado.ID_USUARIO_CREADOR = usuarioId;
                comunicado.FECHA_CREACION = DateTime.Now;

                // Si se solicita envío inmediato, limpiar fecha programada
                if (enviarInmediatamente)
                {
                    comunicado.FECHA_ENVIO_PROGRAMADO = null; // No programar, enviar inmediatamente
                }

                // Deshabilitar el trigger antes de la operación
                await _context.Database.ExecuteSqlRawAsync("DISABLE TRIGGER TR_COMUNICADO_PROGRAMAR ON CRM_COMUNICADO");

                try
                {
                    _context.Add(comunicado);
                    await _context.SaveChangesAsync();

                    // Crear segmentos de comunicado
                    if (provinciasSeleccionadas != null && provinciasSeleccionadas.Count > 0)
                    {
                        foreach (var idProvincia in provinciasSeleccionadas)
                        {
                            var segmento = new SegmentoComunicado
                            {
                                ID_COMUNICADO = comunicado.ID_COMUNICADO,
                                ID_PROVINCIA = idProvincia,
                                ID_TIPO_USUARIO = null
                            };
                            _context.SegmentosComunicado.Add(segmento);
                        }
                    }

                    if (tiposUsuarioSeleccionados != null && tiposUsuarioSeleccionados.Count > 0)
                    {
                        foreach (var idTipoUsuario in tiposUsuarioSeleccionados)
                        {
                            var existeSegmento = await _context.SegmentosComunicado
                                .AnyAsync(s => s.ID_COMUNICADO == comunicado.ID_COMUNICADO && s.ID_TIPO_USUARIO == idTipoUsuario && s.ID_PROVINCIA == null);

                            if (!existeSegmento)
                            {
                                var segmento = new SegmentoComunicado
                                {
                                    ID_COMUNICADO = comunicado.ID_COMUNICADO,
                                    ID_PROVINCIA = null,
                                    ID_TIPO_USUARIO = idTipoUsuario
                                };
                                _context.SegmentosComunicado.Add(segmento);
                            }
                        }
                    }

                    await _context.SaveChangesAsync();

                    // Si se solicita envío inmediato, procesar el envío
                    if (enviarInmediatamente)
                    {
                        try
                        {
                            await EnviarComunicado(comunicado.ID_COMUNICADO, enviarPorCorreo, enviarPorWhatsApp);
                            TempData["Message"] = "Comunicado creado y enviado exitosamente";
                        }
                        catch (Exception ex)
                        {
                            TempData["Message"] = "Comunicado creado exitosamente, pero hubo un error en el envío: " + ex.Message;
                        }
                    }
                    else
                    {
                        TempData["Message"] = comunicado.FECHA_ENVIO_PROGRAMADO.HasValue
                            ? $"Comunicado creado exitosamente. Se enviará automáticamente el {comunicado.FECHA_ENVIO_PROGRAMADO:dd/MM/yyyy 'a las' HH:mm}"
                            : "Comunicado creado exitosamente";
                    }

                    return RedirectToAction(nameof(Index));
                }
                finally
                {
                    // Volver a habilitar el trigger al finalizar
                    await _context.Database.ExecuteSqlRawAsync("ENABLE TRIGGER TR_COMUNICADO_PROGRAMAR ON CRM_COMUNICADO");
                }
            }

            ViewBag.Provincias = _context.Provincias.ToList();
            ViewBag.TiposUsuario = _context.TiposUsuario.ToList();
            return View(comunicado);
        }

        [Authorize]
        public IActionResult Editar(int id)
        {
            // Verificar si el usuario es administrador
            var tipoUsuarioId = HttpContext.Session.GetInt32("TipoUsuarioId");
            if (tipoUsuarioId != 1)
            {
                return RedirectToAction("Index", "Home");
            }

            var comunicado = _context.Comunicados
                .Include(c => c.Segmentos)
                .Include(c => c.UsuarioCreador)
                .FirstOrDefault(c => c.ID_COMUNICADO == id);

            if (comunicado == null)
            {
                return NotFound();
            }

            // Verificar si el comunicado ya fue enviado
            if (comunicado.FECHA_ENVIO_REAL.HasValue)
            {
                TempData["Error"] = "No se puede editar un comunicado que ya ha sido enviado";
                return RedirectToAction(nameof(Index));
            }

            ViewBag.Provincias = _context.Provincias.ToList();
            ViewBag.TiposUsuario = _context.TiposUsuario.ToList();

            // Obtener provincias y tipos de usuario seleccionados
            var provinciasSeleccionadas = comunicado.Segmentos
                .Where(s => s.ID_PROVINCIA.HasValue)
                .Select(s => s.ID_PROVINCIA.Value)
                .ToList();

            var tiposUsuarioSeleccionados = comunicado.Segmentos
                .Where(s => s.ID_TIPO_USUARIO.HasValue)
                .Select(s => s.ID_TIPO_USUARIO.Value)
                .ToList();

            ViewBag.ProvinciasSeleccionadas = provinciasSeleccionadas;
            ViewBag.TiposUsuarioSeleccionados = tiposUsuarioSeleccionados;

            return View(comunicado);
        }

        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Editar(int id, Comunicado comunicado, List<int> provinciasSeleccionadas, List<int> tiposUsuarioSeleccionados, string enviarInmediatamente, bool enviarPorCorreo, bool enviarPorWhatsApp)
        {
            // Verificar si el usuario es administrador
            var tipoUsuarioId = HttpContext.Session.GetInt32("TipoUsuarioId");
            if (tipoUsuarioId != 1)
            {
                return RedirectToAction("Index", "Home");
            }

            if (id != comunicado.ID_COMUNICADO)
            {
                return NotFound();
            }

            var comunicadoOriginal = await _context.Comunicados
                .Include(c => c.Segmentos)
                .FirstOrDefaultAsync(c => c.ID_COMUNICADO == id);

            if (comunicadoOriginal == null)
            {
                return NotFound();
            }

            // Verificar si el comunicado ya fue enviado
            if (comunicadoOriginal.FECHA_ENVIO_REAL.HasValue)
            {
                TempData["Error"] = "No se puede editar un comunicado que ya ha sido enviado";
                return RedirectToAction(nameof(Index));
            }

            // Convertir el valor del checkbox a bool
            bool enviarAhora = !string.IsNullOrEmpty(enviarInmediatamente) &&
                               (enviarInmediatamente.ToLower() == "true" || enviarInmediatamente == "on");

            ModelState.Remove("Envios");
            ModelState.Remove("Segmentos");
            ModelState.Remove("UsuarioCreador");
            ModelState.Remove("enviarInmediatamente");

            // Validación de fecha de envío programado
            if (comunicado.FECHA_ENVIO_PROGRAMADO.HasValue && !enviarAhora)
            {
                // Permitir fechas futuras (al menos 1 minuto en el futuro)
                var fechaMinima = DateTime.Now.AddMinutes(1);
                if (comunicado.FECHA_ENVIO_PROGRAMADO.Value <= fechaMinima)
                {
                    ModelState.AddModelError("FECHA_ENVIO_PROGRAMADO", "La fecha de envío programado debe ser al menos 1 minuto en el futuro.");
                }
            }

            if (ModelState.IsValid)
            {
                try
                {
                    // Actualizar datos del comunicado
                    comunicadoOriginal.TITULO = comunicado.TITULO;
                    comunicadoOriginal.MENSAJE = comunicado.MENSAJE;
                    comunicadoOriginal.FECHA_ENVIO_PROGRAMADO = comunicado.FECHA_ENVIO_PROGRAMADO;

                    // Si se solicita envío inmediato, limpiar fecha programada
                    if (enviarAhora)
                    {
                        comunicadoOriginal.FECHA_ENVIO_PROGRAMADO = null;
                    }

                    // Deshabilitar el trigger antes de la operación
                    await _context.Database.ExecuteSqlRawAsync("DISABLE TRIGGER TR_COMUNICADO_PROGRAMAR ON CRM_COMUNICADO");

                    try
                    {
                        _context.Update(comunicadoOriginal);

                        // Eliminar todos los segmentos existentes
                        foreach (var segmento in comunicadoOriginal.Segmentos.ToList())
                        {
                            _context.SegmentosComunicado.Remove(segmento);
                        }

                        // Crear nuevos segmentos de comunicado
                        if (provinciasSeleccionadas != null && provinciasSeleccionadas.Count > 0)
                        {
                            foreach (var idProvincia in provinciasSeleccionadas)
                            {
                                var segmento = new SegmentoComunicado
                                {
                                    ID_COMUNICADO = comunicado.ID_COMUNICADO,
                                    ID_PROVINCIA = idProvincia,
                                    ID_TIPO_USUARIO = null
                                };
                                _context.SegmentosComunicado.Add(segmento);
                            }
                        }

                        if (tiposUsuarioSeleccionados != null && tiposUsuarioSeleccionados.Count > 0)
                        {
                            foreach (var idTipoUsuario in tiposUsuarioSeleccionados)
                            {
                                var segmento = new SegmentoComunicado
                                {
                                    ID_COMUNICADO = comunicado.ID_COMUNICADO,
                                    ID_PROVINCIA = null,
                                    ID_TIPO_USUARIO = idTipoUsuario
                                };
                                _context.SegmentosComunicado.Add(segmento);
                            }
                        }

                        await _context.SaveChangesAsync();

                        // Si se solicita envío inmediato, procesar el envío
                        if (enviarAhora)
                        {
                            await EnviarComunicado(comunicado.ID_COMUNICADO, enviarPorCorreo, enviarPorWhatsApp);
                        }

                        TempData["Message"] = enviarAhora ?
                            "Comunicado actualizado y enviado exitosamente" :
                            "Comunicado actualizado exitosamente";
                        return RedirectToAction(nameof(Index));
                    }
                    finally
                    {
                        // Volver a habilitar el trigger al finalizar
                        await _context.Database.ExecuteSqlRawAsync("ENABLE TRIGGER TR_COMUNICADO_PROGRAMAR ON CRM_COMUNICADO");
                    }
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!ComunicadoExists(comunicado.ID_COMUNICADO))
                    {
                        return NotFound();
                    }
                    else
                    {
                        throw;
                    }
                }
            }

            ViewBag.Provincias = _context.Provincias.ToList();
            ViewBag.TiposUsuario = _context.TiposUsuario.ToList();
            ViewBag.ProvinciasSeleccionadas = provinciasSeleccionadas;
            ViewBag.TiposUsuarioSeleccionados = tiposUsuarioSeleccionados;
            return View(comunicado);
        }

        [Authorize]
        public IActionResult Eliminar(int id)
        {
            // Verificar si el usuario es administrador
            var tipoUsuarioId = HttpContext.Session.GetInt32("TipoUsuarioId");
            if (tipoUsuarioId != 1)
            {
                return RedirectToAction("Index", "Home");
            }

            var comunicado = _context.Comunicados
                .Include(c => c.UsuarioCreador)
                .FirstOrDefault(c => c.ID_COMUNICADO == id);

            if (comunicado == null)
            {
                return NotFound();
            }

            return View(comunicado);
        }

        [Authorize]
        [HttpPost, ActionName("Eliminar")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ConfirmarEliminar(int id)
        {
            // Verificar si el usuario es administrador
            var tipoUsuarioId = HttpContext.Session.GetInt32("TipoUsuarioId");
            if (tipoUsuarioId != 1)
            {
                return RedirectToAction("Index", "Home");
            }

            var comunicado = await _context.Comunicados
                .Include(c => c.Segmentos)
                .Include(c => c.Envios)
                .FirstOrDefaultAsync(c => c.ID_COMUNICADO == id);

            if (comunicado == null)
            {
                return NotFound();
            }

            // Eliminar segmentos
            foreach (var segmento in comunicado.Segmentos.ToList())
            {
                _context.SegmentosComunicado.Remove(segmento);
            }

            // Eliminar envíos
            foreach (var envio in comunicado.Envios.ToList())
            {
                _context.EnviosComunicado.Remove(envio);
            }

            // Eliminar comunicado
            _context.Comunicados.Remove(comunicado);
            await _context.SaveChangesAsync();

            TempData["Message"] = "Comunicado eliminado exitosamente";
            return RedirectToAction(nameof(Index));
        }

        [Authorize]
        [HttpPost]
        public async Task<IActionResult> EnviarComunicadoManual(int id, bool enviarPorCorreo, bool enviarPorWhatsApp)
        {
            // Verificar si el usuario es administrador
            var tipoUsuarioId = HttpContext.Session.GetInt32("TipoUsuarioId");
            if (tipoUsuarioId != 1)
            {
                return RedirectToAction("Index", "Home");
            }

            try
            {
                await EnviarComunicado(id, enviarPorCorreo, enviarPorWhatsApp);
                TempData["Message"] = "Comunicado enviado exitosamente";
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Error al enviar comunicado: {ex.Message}";
            }

            return RedirectToAction(nameof(Index));
        }

        // MÉTODOS PARA GESTIÓN DE PROGRAMADOS

        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CancelarEnvioProgramado(int id)
        {
            var tipoUsuarioId = HttpContext.Session.GetInt32("TipoUsuarioId");
            if (tipoUsuarioId != 1)
            {
                return RedirectToAction("Index", "Home");
            }

            var comunicado = await _context.Comunicados.FindAsync(id);

            if (comunicado == null)
            {
                TempData["Error"] = "Comunicado no encontrado";
                return RedirectToAction(nameof(Index));
            }

            if (comunicado.FECHA_ENVIO_REAL.HasValue)
            {
                TempData["Error"] = "No se puede cancelar un comunicado que ya fue enviado";
                return RedirectToAction(nameof(Index));
            }

            // Deshabilitar triggers antes de la operación
            await _context.Database.ExecuteSqlRawAsync("DISABLE TRIGGER TR_COMUNICADO_PROGRAMAR ON CRM_COMUNICADO");

            try
            {
                comunicado.FECHA_ENVIO_PROGRAMADO = null;
                await _context.SaveChangesAsync();

                TempData["Message"] = "Envío programado cancelado exitosamente";
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Error al cancelar envío programado: {ex.Message}";
            }
            finally
            {
                // Volver a habilitar triggers
                await _context.Database.ExecuteSqlRawAsync("ENABLE TRIGGER TR_COMUNICADO_PROGRAMAR ON CRM_COMUNICADO");
            }

            return RedirectToAction(nameof(Index));
        }

        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ProcesarComunicadosProgramados()
        {
            var tipoUsuarioId = HttpContext.Session.GetInt32("TipoUsuarioId");
            if (tipoUsuarioId != 1)
            {
                return Json(new { success = false, message = "No autorizado" });
            }

            try
            {
                var ahora = DateTime.Now;
                var comunicadosPendientes = await _context.Comunicados
                    .Where(c => c.FECHA_ENVIO_PROGRAMADO.HasValue &&
                               c.FECHA_ENVIO_PROGRAMADO <= ahora &&
                               !c.FECHA_ENVIO_REAL.HasValue)
                    .ToListAsync();

                var resultados = new List<object>();

                foreach (var comunicado in comunicadosPendientes)
                {
                    try
                    {
                        await EnviarComunicado(comunicado.ID_COMUNICADO, true, true);

                        resultados.Add(new
                        {
                            id = comunicado.ID_COMUNICADO,
                            titulo = comunicado.TITULO,
                            success = true,
                            message = "Enviado exitosamente"
                        });
                    }
                    catch (Exception ex)
                    {
                        resultados.Add(new
                        {
                            id = comunicado.ID_COMUNICADO,
                            titulo = comunicado.TITULO,
                            success = false,
                            message = ex.Message
                        });
                    }
                }

                return Json(new
                {
                    success = true,
                    totalProcesados = comunicadosPendientes.Count,
                    resultados = resultados
                });
            }
            catch (Exception ex)
            {
                return Json(new
                {
                    success = false,
                    message = ex.Message
                });
            }
        }

        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ProcesarComunicadosProgramadosForm()
        {
            var tipoUsuarioId = HttpContext.Session.GetInt32("TipoUsuarioId");
            if (tipoUsuarioId != 1)
            {
                return RedirectToAction("Index", "Home");
            }

            try
            {
                var ahora = DateTime.Now;
                var comunicadosPendientes = await _context.Comunicados
                    .Where(c => c.FECHA_ENVIO_PROGRAMADO.HasValue &&
                               c.FECHA_ENVIO_PROGRAMADO <= ahora &&
                               !c.FECHA_ENVIO_REAL.HasValue)
                    .ToListAsync();

                int procesados = 0;
                foreach (var comunicado in comunicadosPendientes)
                {
                    try
                    {
                        await EnviarComunicado(comunicado.ID_COMUNICADO, true, true);
                        procesados++;
                    }
                    catch (Exception ex)
                    {
                        // Log individual error but continue
                        Console.WriteLine($"Error procesando comunicado {comunicado.ID_COMUNICADO}: {ex.Message}");
                    }
                }

                TempData["Message"] = $"Se procesaron {procesados} de {comunicadosPendientes.Count} comunicados exitosamente.";
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Error al procesar comunicados: {ex.Message}";
            }

            return RedirectToAction(nameof(Index));
        }

        // Método para enviar un comunicado a los destinatarios
        private async Task EnviarComunicado(int idComunicado, bool enviarPorCorreo = true, bool enviarPorWhatsApp = true)
        {
            var comunicado = await _context.Comunicados
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
            var usuariosQuery = _context.Usuarios.AsQueryable();

            // Si no hay segmentos, enviar a todos los usuarios de tipo cliente
            if (segmentos.Count == 0)
            {
                var tipoClienteId = await _context.TiposUsuario
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

            // Crear registros de envío
            foreach (var usuario in usuarios)
            {
                var envioExistente = await _context.EnviosComunicado
                    .AnyAsync(e => e.ID_COMUNICADO == idComunicado && e.ID_USUARIO_DESTINATARIO == usuario.IDENTIFICACION);

                if (!envioExistente)
                {
                    var envio = new EnvioComunicado
                    {
                        ID_COMUNICADO = idComunicado,
                        ID_USUARIO_DESTINATARIO = usuario.IDENTIFICACION,
                        FECHA_ENVIO = DateTime.Now
                    };

                    _context.EnviosComunicado.Add(envio);

                    // Enviar por correo si está habilitado y el usuario tiene email
                    if (enviarPorCorreo && !string.IsNullOrEmpty(usuario.CORREO_ELECTRONICO))
                    {
                        await EnviarPorCorreo(usuario.CORREO_ELECTRONICO, comunicado.TITULO, comunicado.MENSAJE);
                    }

                    // Enviar por WhatsApp si está habilitado y el usuario tiene teléfono
                    if (enviarPorWhatsApp && !string.IsNullOrEmpty(usuario.TELEFONO))
                    {
                        await EnviarPorWhatsApp(usuario.TELEFONO, comunicado.TITULO, comunicado.MENSAJE);
                    }
                }
            }

            // Actualizar fecha de envío real
            comunicado.FECHA_ENVIO_REAL = DateTime.Now;
            _context.Update(comunicado);

            await _context.SaveChangesAsync();
        }

        // Método para enviar por correo electrónico
        private async Task EnviarPorCorreo(string email, string titulo, string mensaje)
        {
            try
            {
                using (var client = new System.Net.Mail.SmtpClient(_configuration["Email:SmtpServer"]))
                {
                    client.Port = int.Parse(_configuration["Email:Port"]);
                    client.Credentials = new System.Net.NetworkCredential(
                        _configuration["Email:Username"],
                        _configuration["Email:Password"]);
                    client.EnableSsl = true;

                    var mailMessage = new System.Net.Mail.MailMessage
                    {
                        From = new System.Net.Mail.MailAddress(_configuration["Email:FromAddress"]),
                        Subject = titulo,
                        Body = mensaje,
                        IsBodyHtml = true
                    };

                    mailMessage.To.Add(email);
                    await client.SendMailAsync(mailMessage);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error al enviar correo: {ex.Message}");
            }
        }

        // Método para enviar por WhatsApp
        private async Task<(bool Success, string Message)> EnviarPorWhatsApp(string telefono, string titulo, string mensaje)
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
                string apiUrl = _configuration["WhatsAppSettings:ApiUrl"];
                string phoneNumberId = _configuration["WhatsAppSettings:PhoneNumberId"];
                string accessToken = _configuration["WhatsAppSettings:AccessToken"];

                // En un entorno de desarrollo/prueba, podemos simular el envío
                if (string.IsNullOrEmpty(apiUrl) || string.IsNullOrEmpty(phoneNumberId) || string.IsNullOrEmpty(accessToken))
                {
                    return (true, "Mensaje simulado (entorno de desarrollo)");
                }

                // Construir la URL completa
                string url = $"{apiUrl}{phoneNumberId}/messages";

                // Construir el objeto de datos para la solicitud
                var requestData = new
                {
                    messaging_product = "whatsapp",
                    recipient_type = "individual",
                    to = telefono,
                    type = "text",
                    text = new
                    {
                        body = mensajeWhatsApp
                    }
                };

                // Convertir a JSON
                string jsonContent = JsonConvert.SerializeObject(requestData);
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                // Configurar encabezados
                using (var httpClient = new HttpClient())
                {
                    httpClient.DefaultRequestHeaders.Clear();
                    httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {accessToken}");

                    // Enviar solicitud
                    var response = await httpClient.PostAsync(url, content);

                    if (response.IsSuccessStatusCode)
                    {
                        return (true, "Mensaje enviado correctamente");
                    }
                    else
                    {
                        string errorResponse = await response.Content.ReadAsStringAsync();
                        return (false, $"Error HTTP {response.StatusCode}: {errorResponse}");
                    }
                }
            }
            catch (Exception ex)
            {
                return (false, $"Excepción: {ex.Message}");
            }
        }

        // Método VerComunicado corregido
        [Authorize]
        public IActionResult VerComunicado(int id, int? usuarioId = null)
        {
            var usuarioActualId = HttpContext.Session.GetInt32("UsuarioId");
            var tipoUsuarioId = HttpContext.Session.GetInt32("TipoUsuarioId");

            if (!usuarioActualId.HasValue)
            {
                return RedirectToAction("Login", "Account");
            }

            var comunicado = _context.Comunicados
                .Include(c => c.UsuarioCreador)
                .FirstOrDefault(c => c.ID_COMUNICADO == id);

            if (comunicado == null)
            {
                return NotFound();
            }

            bool esAdministrador = tipoUsuarioId == 1;

            if (!esAdministrador)
            {
                // Para usuarios regulares, verificar que el comunicado les fue enviado
                var envio = _context.EnviosComunicado
                    .FirstOrDefault(e => e.ID_COMUNICADO == id && e.ID_USUARIO_DESTINATARIO == usuarioActualId);

                if (envio == null)
                {
                    return View("AccessDenied");
                }
            }

            // Si es administrador, agregar información adicional al ViewBag
            if (esAdministrador && usuarioId.HasValue)
            {
                var usuarioObjetivo = _context.Usuarios
                    .Include(u => u.TipoUsuario)
                    .FirstOrDefault(u => u.IDENTIFICACION == usuarioId);

                ViewBag.UsuarioObjetivo = usuarioObjetivo;
                ViewBag.ViendoComoAdmin = true;
            }

            return View(comunicado);
        }

        // Método nuevo para que administradores vean comunicados por usuario
        [Authorize]
        public IActionResult ComunicadosPorUsuario(int usuarioId)
        {
            var tipoUsuarioId = HttpContext.Session.GetInt32("TipoUsuarioId");

            // Solo administradores pueden ver comunicados de otros usuarios
            if (tipoUsuarioId != 1)
            {
                return View("AccessDenied");
            }

            var usuario = _context.Usuarios
                .Include(u => u.TipoUsuario)
                .Include(u => u.Provincia)
                .FirstOrDefault(u => u.IDENTIFICACION == usuarioId);

            if (usuario == null)
            {
                return NotFound();
            }

            var comunicados = _context.EnviosComunicado
                .Include(e => e.Comunicado)
                .ThenInclude(c => c.UsuarioCreador)
                .Where(e => e.ID_USUARIO_DESTINATARIO == usuarioId)
                .OrderByDescending(e => e.FECHA_ENVIO)
                .Select(e => e.Comunicado)
                .ToList();

            ViewBag.Usuario = usuario;
            return View("MisComunicados", comunicados);
        }

        // Método mejorado MisComunicados para manejar vista de administrador
        [Authorize]
        public IActionResult MisComunicados(int? usuarioId = null)
        {
            var usuarioActualId = HttpContext.Session.GetInt32("UsuarioId");
            var tipoUsuarioId = HttpContext.Session.GetInt32("TipoUsuarioId");

            if (!usuarioActualId.HasValue)
            {
                return RedirectToAction("Login", "Account");
            }

            // Determinar de qué usuario mostrar los comunicados
            int targetUsuarioId = usuarioActualId.Value;
            bool viendoComoAdmin = false;

            if (usuarioId.HasValue && tipoUsuarioId == 1)
            {
                // Es administrador viendo comunicados de otro usuario
                targetUsuarioId = usuarioId.Value;
                viendoComoAdmin = true;

                var usuarioObjetivo = _context.Usuarios
                    .Include(u => u.TipoUsuario)
                    .FirstOrDefault(u => u.IDENTIFICACION == targetUsuarioId);

                if (usuarioObjetivo == null)
                {
                    return NotFound();
                }

                ViewBag.UsuarioObjetivo = usuarioObjetivo;
            }
            else if (usuarioId.HasValue && tipoUsuarioId != 1)
            {
                // Usuario regular intentando ver comunicados de otro usuario
                return View("AccessDenied");
            }

            var comunicados = _context.EnviosComunicado
                .Include(e => e.Comunicado)
                .ThenInclude(c => c.UsuarioCreador)
                .Where(e => e.ID_USUARIO_DESTINATARIO == targetUsuarioId)
                .OrderByDescending(e => e.FECHA_ENVIO)
                .Select(e => e.Comunicado)
                .ToList();

            ViewBag.ViendoComoAdmin = viendoComoAdmin;
            return View(comunicados);
        }

        private bool ComunicadoExists(int id)
        {
            return _context.Comunicados.Any(e => e.ID_COMUNICADO == id);
        }
    }
}