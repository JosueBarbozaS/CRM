
\# README - IngeTechCRM

\## Descripción del Proyecto

IngeTechCRM es un sistema de gestión de relaciones con clientes (CRM) diseñado para optimizar la administración de procesos internos, mejorar la atención al cliente y fortalecer la toma de decisiones mediante el acceso a información precisa y en tiempo real. El sistema centraliza funciones clave como la gestión de usuarios, inventario, productos, soporte técnico, marketing y reportes.

\## Módulos Principales

1. \*\*Gestión de Usuarios\*\*: Registro, autenticación, recuperación de contraseña, asignación de roles y permisos.
1. \*\*Gestión de Productos\*\*: Alta, modificación, eliminación y consulta de productos.
1. \*\*Gestión de Inventario\*\*: Control de existencias, movimientos de entrada/salida, alertas por bajo stock.
1. \*\*Chat de Soporte Técnico\*\*: Canal de comunicación en tiempo real para resolver consultas o incidentes.
1. \*\*Gestión de Marketing\*\*: Seguimiento de campañas promocionales y segmentación de clientes.
1. \*\*Generación de Reportes\*\*: Estadísticas de ventas, inventario, atención al cliente y otras métricas clave.

\## Requisitos Previos

- \*\*Visual Studio Community\*\*: Asegúrate de tener instalada la versión más reciente de Visual Studio Community.
- \*\*.NET Framework\*\*: Este proyecto requiere .NET Framework (especificar la versión si es necesario).
- \*\*SQL Server\*\*: Necesitarás una instancia de SQL Server para la base de datos.

\## Instalación

\### 1. Clonar el Repositorio

\```bash

git clone https://github.com/tu\_usuario/IngeTechCRM.git

\```

\### 2. Abrir el Proyecto

- Abre Visual Studio Community.
- Selecciona "Abrir un proyecto o una solución".
- Navega hasta la carpeta donde clonaste el repositorio y selecciona el archivo `.sln`.

\### 3. Restaurar Paquetes NuGet

- En el menú de Visual Studio, ve a:

\```

Herramientas > Administrador de paquetes NuGet > Consola del Administrador de paquetes

\```

- Ejecuta el siguiente comando para restaurar los paquetes:

\```powershell

Update-Package -Reinstall

\```

\### 4. Configurar la Base de Datos

- Asegúrate de que tu instancia de SQL Server esté en funcionamiento.
- Crea una base de datos nueva (por ejemplo, `IngeTechCRM`).
- Actualiza la cadena de conexión en el archivo `appsettings.json` o `web.config` según sea necesario.

\### 5. Ejecutar Migraciones (si usas Entity Framework)

- En la consola del Administrador de paquetes, ejecuta:

\```powershell

Update-Database

\```

\### 6. Ejecutar la Aplicación

- Presiona `F5` o selecciona \*\*Iniciar depuración\*\* para ejecutar la aplicación en tu navegador.

\## Estándares de Código

- \*\*Variables de Campos\*\*: `MAYÚSCULAS\_CON\_GUIONES\_BAJOS`
- \*\*Clases de Modelos\*\*: `PascalCase` + sufijo descriptivo
- \*\*Propiedades de Navegación\*\*: `PascalCase`
- \*\*Campos de Clave Primaria\*\*: `ID\_[NOMBRE\_ENTIDAD]`
- \*\*Campos de Clave Foránea\*\*: `ID\_[ENTIDAD\_REFERENCIADA]`

\### Atributos de Data Annotations

- `[Key]`: En todas las claves primarias
- `[Required]`: En campos obligatorios
- `[StringLength]`: En campos de texto con límite
- `[Display(Name)]`: En todos los campos visibles
- `[DataType]`: Para fechas, monedas, passwords

\## Pruebas Unitarias

Se han realizado pruebas unitarias para verificar la funcionalidad de los módulos, asegurando que cada componente cumpla con los requisitos establecidos. Las pruebas incluyen:

- Registro de usuarios
- Gestión de productos
- Control de inventario
- Generación de reportes
