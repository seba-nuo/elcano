# Para qué sirve la carpeta scripts

Contiene atajos de PowerShell para **desarrollar, probar y distribuir** MinioDesk. No son servicios en segundo plano ni se ejecutan al abrir el cliente portable. Si solo quieres usar la aplicación, abre `artifacts/MinioDesk-win-x64/MinioDesk.exe`.

Ejecuta los comandos desde la raíz del repositorio:

| Archivo | Qué hace | Comando |
| --- | --- | --- |
| `common.ps1` | Configura el SDK local o instalado, las cachés de NuGet y la función que comprueba errores de `dotnet`. Lo cargan los otros scripts. | No hace falta ejecutarlo por separado. |
| `run.ps1` | Compila si hay cambios y abre la aplicación WPF en modo Release. | `.\scripts\run.ps1` |
| `publish.ps1` | Prepara la extensión nativa `httpfs` y genera una carpeta portable para Windows x64, incluyendo .NET y DuckDB. | `.\scripts\publish.ps1` |
| `test.ps1` | Ejecuta comprobaciones de perfiles cifrados y Parquet local. Con `-Integration`, también prueba los dos servidores locales usando buckets temporales que elimina al terminar. | `.\scripts\test.ps1` o `.\scripts\test.ps1 -Integration` |
| `seed-samples.ps1` | Genera cinco Parquet sintéticos por servidor, los sube y verifica su vista previa remota. Los ejemplos permanecen en MinIO para que puedas abrirlos. | `.\scripts\seed-samples.ps1 -UseLocalLabCredentials` |

Las cachés, archivos generados y copias locales de los ejemplos se guardan bajo `.local/`, excluido de Git. La aplicación publicada se guarda en `artifacts/`. La primera restauración de paquetes o descarga de `httpfs` requiere Internet.

## Ejemplos Parquet persistentes

El generador usa por defecto `http://localhost:9000` y `http://localhost:9100`, con región `us-east-1`. Crea el bucket **`miniodesk-demo`** si aún no existe. Cada ejecución crea una carpeta `muestras-FECHA-HORA-ID/` diferente para conservar los ejemplos de ejecuciones anteriores.

| Archivo | Filas | Qué permite probar |
| --- | ---: | --- |
| `01_ventas_2026.parquet` | 240 | Fechas, importes decimales, booleanos y el límite de 200 filas de la vista previa. |
| `02_clientes_unicode.parquet` | 30 | Acentos, caracteres chinos y valores nulos. Todos los datos son ficticios. |
| `03_tipos_y_nulos.parquet` | 12 | Tipos numéricos, fecha/hora, nulos, listas y estructuras anidadas. |
| `04_eventos_100000.parquet` | 100 000 | Un archivo con más filas que la vista previa, comprimido con ZSTD. |
| `05_vacio_con_esquema.parquet` | 0 | Un Parquet vacío que conserva nombres y tipos de columnas. |

Los archivos incluyen `servidor_origen`, que identifica el endpoint y ayuda a comprobar que estás mirando el MinIO seleccionado. El generador verifica cada archivo mediante el mismo lector remoto que usa la aplicación. Deja un `manifest.json` con las claves S3, cantidades de filas, columnas y tamaños dentro de `.local/sample-data/ID/`.

Con `-UseLocalLabCredentials`, el script lee únicamente `MINIO_ROOT_USER` y `MINIO_ROOT_PASSWORD` de `.local/minio-lab/.env`. No muestra esas claves y restaura las variables de entorno anteriores al finalizar. No ejecuta el contenido del `.env`.

Para usar otras credenciales, define `MINIODESK_SAMPLE_ACCESS_KEY` y `MINIODESK_SAMPLE_SECRET_KEY` en tu sesión y omite ese modificador. Puedes indicar `-Endpoints`, `-Bucket` y `-Region`. Estas operaciones necesitan permisos para listar buckets, crear el bucket si no existe, subir objetos y leerlos.

En MinioDesk: conecta el servidor, selecciona **miniodesk-demo**, entra en la carpeta **muestras-…** y haz doble clic en un Parquet. Si el bucket se creó después de conectar, vuelve a pulsar **Conectar** para recargar la lista de buckets.
