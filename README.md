# MinioDesk

Aplicación de escritorio para **Windows** que permite conectarse a **MinIO**, explorar archivos, subir y descargar objetos y visualizar archivos **Parquet**. Está desarrollada en C# con WPF y utiliza DuckDB integrado para leer Parquet.

## Funcionalidades

- Crear, editar, probar y alternar perfiles de conexión.
- Guardar las credenciales cifradas para el usuario actual de Windows.
- Explorar buckets y carpetas virtuales, con navegación por páginas.
- Filtrar y ordenar los archivos de la página actual.
- Subir archivos con progreso, cancelación y carga multipart para archivos grandes.
- Descargar archivos por streaming.
- Consultar el esquema y una vista previa de un Parquet remoto o local.

## Requisitos para usar la aplicación

| Requisito | Detalle |
| --- | --- |
| Sistema operativo | Windows con entorno de escritorio y compatible con .NET 10. Se recomienda Windows 11. Consulta las [versiones de Windows compatibles](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md#windows). |
| Arquitectura | La distribución actual es **Windows x64**, para procesadores Intel o AMD de 64 bits. |
| Conectividad | Acceso de red al endpoint S3 del servidor MinIO, mediante red local, Internet o una VPN según su ubicación. |
| Credenciales | Access key y secret key con permisos sobre los buckets y archivos que se utilizarán. |
| Almacenamiento | Espacio para la carpeta de la aplicación y los archivos que se descarguen. |

La distribución portable incluye el runtime de .NET, DuckDB y la extensión de lectura remota. No requiere instalar el SDK de .NET, Python, Docker ni un servidor de base de datos para usarla.

La memoria necesaria depende de los archivos consultados. La vista previa limita los resultados, pero no se ha establecido un requisito mínimo de RAM mediante pruebas en distintos equipos.

## Ejecutar la aplicación portable

1. Obtén la carpeta **MinioDesk-win-x64**, generada mediante los pasos de publicación de este README.
2. Si se distribuye como ZIP, extrae todo su contenido en una carpeta del equipo.
3. Mantén juntos **MinioDesk.exe**, las DLL y la carpeta **extensions**.
4. Abre **MinioDesk.exe**.

Debes copiar la carpeta completa, no únicamente el ejecutable. La distribución actual es portable y no incluye un instalador ni actualización automática.

## Configurar una conexión a MinIO

Solicita al administrador del servidor estos datos:

| Campo | Qué introducir |
| --- | --- |
| Nombre | Un nombre para identificar la conexión. |
| Endpoint S3 | La dirección completa de la API, por ejemplo **https://minio.ejemplo.com**. Puede incluir un puerto si el servidor lo requiere. |
| Región | La región configurada en el servidor. Usa **us-east-1** únicamente si corresponde a esa instalación. |
| Access key | La clave de acceso del usuario o cuenta de servicio. |
| Secret key | El secreto asociado a la clave de acceso. |
| Bucket fijo | Opcional. Indica un bucket si las credenciales no permiten listar todos los buckets. |

Para crear el perfil:

1. Pulsa **+** en el área de conexiones de la esquina superior derecha.
2. Completa los datos del servidor.
3. Pulsa **Probar conexión**.
4. Pulsa **Guardar**.
5. Selecciona el perfil en el desplegable y pulsa **Conectar**.
6. Elige un bucket para explorar sus archivos.

Utiliza el endpoint de la **API S3**, que puede ser diferente de la dirección de la consola web de MinIO. Incluye el esquema HTTP o HTTPS y el servidor, sin añadir el nombre del bucket ni rutas de archivos.

Para HTTPS, el certificado debe ser válido y de confianza en el equipo. Si se utiliza una autoridad de certificación privada, sigue las instrucciones de tu organización para instalar su certificado.

## Uso básico

- **Carpetas:** haz doble clic para entrar y utiliza **↑** para subir al nivel anterior.
- **Actualizar:** recarga el contenido de la carpeta actual.
- **Filtrar:** busca nombres entre los elementos de la página actual.
- **Anterior / Siguiente:** cambia de página, de hasta 200 elementos cada una.
- **Subir:** selecciona archivos y confirma su carga en la carpeta actual. Si existe una clave con el mismo nombre, su contenido actual se reemplaza.
- **Descargar:** selecciona un archivo y elige dónde guardarlo.
- **Ver Parquet:** selecciona un archivo Parquet o haz doble clic sobre él.
- **Abrir Parquet local:** visualiza un archivo del equipo sin conectarte a un servidor.

Las conexiones se seleccionan en el desplegable de la esquina superior derecha; el menú **⋯** permite editar o quitar el perfil seleccionado. El explorador ocupa el panel izquierdo y el visor muestra los datos en el área central.

Para cambiar de servidor, selecciona otro perfil y pulsa **Conectar**. Si se crean nuevos buckets después de conectar, pulsa de nuevo **Conectar** para actualizar la lista.

## Visualización de Parquet

La vista previa muestra hasta **200 filas**, **64 columnas** y **512 caracteres por celda**. La pestaña **Esquema** muestra los nombres y tipos originales de las columnas, incluso si el archivo no contiene filas.

DuckDB se ejecuta integrado y las consultas se realizan en segundo plano. La extensión **httpfs** permite leer archivos remotos sin importarlos previamente a una base de datos.

DuckDB utiliza dos hilos y un presupuesto de buffer de 256 MB. Ese presupuesto no limita toda la memoria de la aplicación. La cantidad de datos leídos depende de la estructura del Parquet; el límite de filas no garantiza una cantidad fija de bytes transferidos.

Los valores de la vista previa se presentan como texto. Para claves remotas que contengan asteriscos, interrogaciones o corchetes, descarga el archivo y utiliza **Abrir Parquet local**.

La distribución publicada incluye **httpfs**. Al ejecutar desde el código fuente sin una copia preparada de la extensión, la primera lectura remota necesitará Internet para descargarla.

## Compilar y ejecutar desde el código fuente

Requisitos adicionales de desarrollo:

- [SDK de .NET 10 para Windows x64](https://dotnet.microsoft.com/download/dotnet/10.0). Instalar únicamente el runtime no permite compilar.
- PowerShell para ejecutar los scripts.
- Internet para restaurar las dependencias de NuGet y preparar la extensión **httpfs**.
- Una copia del repositorio, obtenida mediante Git o descargada como ZIP.

Abre PowerShell en la carpeta que contiene **MinioDesk.slnx** y comprueba que aparece un SDK **10.0.x**:

~~~powershell
dotnet --list-sdks
~~~

Para compilar y abrir la aplicación:

~~~powershell
.\scripts\run.ps1
~~~

También puedes abrir la solución en un entorno de desarrollo compatible con .NET 10 y WPF.

## Generar una distribución para otro PC Windows

Desde la raíz del repositorio:

~~~powershell
.\scripts\publish.ps1
~~~

El script prepara **httpfs** y genera la distribución autocontenida en:

~~~text
artifacts/MinioDesk-win-x64/
~~~

Copia **toda esa carpeta** al otro equipo o comprímela como ZIP. El usuario final abre **MinioDesk.exe** y configura sus propias conexiones; no necesita instalar .NET por separado.

La publicación actual está dirigida a Windows x64. No se generan paquetes para Windows de 32 bits ni una compilación nativa ARM64.

## Scripts y estructura del proyecto

| Ruta | Contenido |
| --- | --- |
| src/MinioDesk.App/ | Ventanas WPF y estado de la interfaz. |
| src/MinioDesk.Core/ | Perfiles, cifrado, operaciones S3 y lectura Parquet. |
| tests/MinioDesk.Smoke/ | Comprobaciones del cliente. |
| tools/MinioDesk.Samples/ | Utilidad opcional para generar datos sintéticos. |
| scripts/ | Atajos de desarrollo, pruebas y publicación. |

Comandos principales:

~~~powershell
# Compilar y abrir la aplicación
.\scripts\run.ps1

# Generar la distribución portable
.\scripts\publish.ps1

# Ejecutar las comprobaciones locales
.\scripts\test.ps1
~~~

Los scripts guardan sus herramientas y cachés en **.local/**, excluido de Git. No son necesarios para usar la aplicación portable. Consulta [scripts/README.md](scripts/README.md) para conocer las utilidades adicionales.

## Configuración y credenciales

Los perfiles se guardan en:

~~~text
%LOCALAPPDATA%\MinioDesk\connections.json
~~~

Los nombres, endpoints, región y bucket se guardan como metadatos. Las claves de acceso se cifran con DPAPI para el usuario actual de Windows. Cada usuario o equipo debe configurar sus propias credenciales; copiar ese archivo no constituye un método de migración de secretos.

Las extensiones descargadas durante la ejecución se guardan en **%LOCALAPPDATA%\MinioDesk\extensions**. Si se incluye **extensions/httpfs.duckdb_extension** junto al ejecutable, se utiliza esa copia.

## Solución de problemas

| Problema | Qué comprobar |
| --- | --- |
| La aplicación no abre | Compatibilidad del sistema, arquitectura x64 y extracción de la distribución completa. |
| No se encuentra un SDK al compilar | Instala el SDK de .NET 10 y abre una nueva ventana de PowerShell. |
| No conecta al servidor | Endpoint S3, puerto, acceso a la red o VPN. |
| Acceso denegado | Credenciales y permisos. Si el acceso está limitado, configura **Bucket fijo**. |
| Error de certificado | Nombre del servidor y confianza en la autoridad que emitió el certificado. |
| No se puede visualizar un Parquet remoto | Permisos de lectura y presencia de **httpfs**. Si necesita descargarse, comprueba el acceso a Internet. |
| No aparecen archivos al filtrar | El filtro solo se aplica a la página actual. Borra el filtro o cambia de página. |
