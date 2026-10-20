# MinioDesk

Aplicación de escritorio para **Windows** que permite conectarse a **MinIO**, explorar archivos, subir y descargar objetos y visualizar archivos **Parquet, CSV y TXT**. Está desarrollada en C# con WPF y utiliza DuckDB integrado para consultar tablas.

## Funcionalidades

- Crear, editar, probar y alternar perfiles de conexión.
- Guardar las credenciales cifradas para el usuario actual de Windows.
- Explorar buckets y carpetas virtuales, con navegación por páginas.
- Filtrar y ordenar los archivos de la página actual.
- Subir archivos con progreso, cancelación y carga multipart para archivos grandes.
- Descargar archivos por streaming y eliminar archivos desde su menú contextual.
- Consultar Parquet y CSV remotos o locales con SQL, vista previa y autocompletado de columnas.
- Abrir TXT como texto y reutilizar vistas recientes mediante una caché limitada en memoria.
- Alternar entre tema claro y oscuro, con la preferencia guardada en el equipo.

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

- **Carpetas:** haz doble clic para entrar, utiliza **↑** para subir o pulsa un segmento de la ruta para volver a ese nivel. En rutas profundas, **…** abre un menú con todos los antecesores; los últimos niveles permanecen visibles. La ruta completa situada debajo se puede seleccionar y copiar.
- **Actualizar:** pulsa el icono de flecha circular junto al desplegable del bucket para recargar la carpeta actual.
- **Filtrar:** busca nombres entre los elementos de la página actual.
- **Anterior / Siguiente:** cambia de página, de hasta 200 elementos cada una. El pie del explorador muestra **Página X de Y** y el total de archivos y subcarpetas del nivel actual. El recuento se hace en segundo plano, sin recorrer el contenido de las subcarpetas; si no está disponible se conserva la navegación por páginas. Pulsa **Actualizar** para renovar los totales.
- **Subir:** pulsa el icono de flecha hacia arriba junto a la ruta de la carpeta, selecciona archivos y confirma la carga. Si existe una clave con el mismo nombre, su contenido actual se reemplaza.
- **Descargar:** haz clic secundario sobre un archivo, elige **Descargar archivo** y selecciona dónde guardarlo.
- **Eliminar:** haz clic secundario sobre un archivo y elige **Eliminar archivo**. La aplicación pide confirmación e indica la conexión, el bucket y la clave del objeto. El resultado en buckets con versionado depende de la configuración del servidor.
- **Ver un archivo:** haz doble clic sobre un Parquet, CSV o TXT, o selecciónalo y pulsa **Enter**. Un indicador giratorio aparece mientras se abre.
- **Abrir archivo local:** visualiza un Parquet, CSV o TXT del equipo sin conectarte a un servidor.
- **Tema:** pulsa la luna para activar el modo oscuro o el sol para volver al modo claro.

Las conexiones se seleccionan en el desplegable de la esquina superior derecha; el menú **⋯** permite editar o quitar el perfil seleccionado. El estado **Conectado / Sin conexión** aparece abajo a la derecha: **Sin conexión** se muestra en rojo. El explorador ocupa el panel izquierdo y el visor muestra los datos en el área central.

Para cambiar de servidor, selecciona otro perfil y pulsa **Conectar**. Si se crean nuevos buckets después de conectar, pulsa de nuevo **Conectar** para actualizar la lista.

## Visualización de archivos

La vista previa muestra hasta **200 filas**, **64 columnas** y **512 caracteres por celda**. La pestaña **Esquema** muestra los nombres y tipos originales de las columnas, incluso si el archivo no contiene filas.

El encabezado del visor muestra la ruta completa del archivo abierto, que se puede seleccionar y copiar. En **Datos** y **Esquema** puedes seleccionar texto dentro de una celda o seleccionar varias celdas y usar **Ctrl+C**. El contenido continúa siendo de solo lectura. TXT también permite seleccionar y copiar su texto.

Los CSV se leen como tablas UTF-8, con detección automática de separador, cabecera y tipos. Se admiten valores entre comillas y campos con saltos de línea. La detección de tipos utiliza una muestra de 2048 filas y cada línea CSV se limita a 1 MiB; archivos con tipos inconsistentes o formatos ambiguos pueden necesitar normalización antes de abrirlos.

Los TXT se muestran como texto de solo lectura, conservando saltos de línea y tabulaciones. La lectura utiliza UTF-8 y detecta BOM para otras codificaciones como UTF-16. Se muestran hasta **131 072 caracteres** y se indica cuando la vista es parcial. TXT no utiliza el editor SQL.

### Caché de vistas

Las vistas abiertas se reutilizan al volver al mismo archivo, conservando la consulta y el último resultado. La caché está en memoria, con un presupuesto estimado de **16 MiB** y un máximo de **12 vistas**; se descartan primero las menos recientes. No conserva copias completas de los archivos ni permanece al cerrar la aplicación.

Se vacía al pulsar **Actualizar**, reconectar, cambiar de conexión o bucket, y al iniciar una subida o eliminación. Navegar por carpetas o páginas conserva la caché. Para recoger cambios realizados desde otro cliente, pulsa **Actualizar**. Los archivos locales también se identifican por su tamaño y fecha de modificación.

### Consultas SQL

Al abrir un Parquet o CSV, el editor permite consultarlo como la tabla **archivo**:

~~~sql
SELECT *
FROM archivo
LIMIT 100
~~~

Puedes seleccionar columnas, filtrar con **WHERE**, ordenar, agrupar y utilizar CTE con **WITH**. Las consultas trabajan sobre el archivo completo; la tabla de resultados muestra como máximo 200 filas.

- Pulsa **Ejecutar** o **Ctrl+Enter** para actualizar los resultados.
- El editor empieza con una sola línea. Arrastra el tirador situado debajo del input para aumentar o reducir su altura.
- El editor colorea palabras SQL, nombres entre comillas, funciones, números, cadenas y comentarios, con colores adaptados al tema claro u oscuro. Conserva los atajos de selección, copia y deshacer.
- Activa **Vista previa automática** para ejecutar al dejar de escribir durante aproximadamente un segundo.
- Escribe parte de un nombre o pulsa **Ctrl+Espacio** para ver columnas, tipos y palabras SQL. Usa las flechas para elegir y **Tab** o **Enter** para insertar; **Esc** cierra las sugerencias.
- Pulsa **Restablecer** para volver a la consulta inicial.

Se admite una consulta **SELECT** o **WITH** a la vez. Las consultas se limitan a 60 segundos y pueden interrumpirse con **Cancelar**. Si hay un error SQL, se muestra debajo del editor y se conserva el último resultado válido. El esquema y el autocompletado siguen mostrando las columnas originales del archivo. El alias **parquet** continúa disponible por compatibilidad con consultas anteriores.

DuckDB se ejecuta integrado y las consultas se realizan en segundo plano. La extensión **httpfs** permite leer archivos remotos sin importarlos previamente a una base de datos.

DuckDB utiliza dos hilos y un presupuesto de buffer de 256 MB. Ese presupuesto no limita toda la memoria de la aplicación. La cantidad de datos leídos depende de la estructura del Parquet; el límite de filas no garantiza una cantidad fija de bytes transferidos.

Los valores de las tablas se presentan como texto. Para claves remotas de Parquet o CSV que contengan asteriscos, interrogaciones o corchetes, descarga el archivo y utiliza **Abrir archivo local**.

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
| src/MinioDesk.Core/ | Perfiles, cifrado, operaciones S3, caché y lectura de archivos. |
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

La preferencia de tema claro u oscuro se guarda en **%LOCALAPPDATA%\MinioDesk\appearance.json**.

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
