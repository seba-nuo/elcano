# Avisos de terceros de Elcano

El código propio de Elcano se distribuye bajo la licencia MIT del archivo
`LICENSE`. Las dependencias conservan sus licencias, titulares de derechos,
avisos y condiciones originales. La licencia de Elcano no sustituye las de
esos componentes ni concede derechos sobre sus marcas.

| Componente distribuido | Versión revisada | Licencia principal | Documentos |
| --- | --- | --- | --- |
| AvalonEdit | 6.3.1.120 | MIT | `licenses/AvalonEdit/` |
| AWSSDK.S3 / AWSSDK.Core | 4.0.104.4 / 4.0.102.11 | Apache-2.0 y avisos de componentes internos | `licenses/AWSSDK/` |
| Apache.Arrow / Apache.Arrow.Scalars | 23.0.0 | Apache-2.0 | `licenses/Arrow/` |
| DuckDB.NET.Data.Full / DuckDB.NET.Bindings.Full | 1.5.6.1 | MIT | `licenses/DuckDB.NET/` |
| DuckDB nativo | v1.5.6, revisión `069cc9f9b5` | MIT y licencias de componentes internos | `licenses/DuckDB/` |
| Extensión httpfs | revisión `4bc690dba4496c765777a0269d48fdbaff7cdc11` | MIT y licencias de bibliotecas enlazadas | `licenses/httpfs/`, `licenses/curl/`, `licenses/OpenSSL/`, `licenses/zlib/` |
| Runtime .NET / System.Security.Cryptography.ProtectedData | 10.0.13 / 10.0.0 | MIT y avisos de componentes internos | `licenses/dotnet-runtime/` |
| Runtime Windows Desktop / WPF / Windows Forms | 10.0.13 | MIT y avisos de componentes internos | `licenses/dotnet-desktop/` |

Los documentos de DuckDB conservan las licencias de sus componentes vendorizados,
incluidos los textos MIT, BSD, Apache, Boost, zlib y Unicode aplicables. El
subcomponente Mbed TLS ofrece Apache-2.0 **o** GPL-2.0-or-later: esta distribución
elige **Apache-2.0** y conserva el documento original con ambas alternativas.
La presencia de ese documento no cambia la licencia del código propio de Elcano.

El código fuente de httpfs declara OpenSSL y curl como dependencias. Su
configuración de compilación usa la referencia vcpkg `2025.12.12`, con curl
8.17.0, OpenSSL 3.6.0 y zlib 1.3.1. Se conservan también los avisos ISC y BSD
de los archivos internos de curl. La revisión y SHA-256 de los binarios
distribuidos están registrados en `licenses/manifest.json`.

Algunos avisos originales enumeran componentes opcionales o de otras plataformas;
se conservan como fueron publicados, sin afirmar que todos se ejecuten en Windows.
Las fuentes y sumas SHA-256 de cada documento se indican en ese mismo manifiesto.

This product includes software developed by vbAccelerator (http://vbaccelerator.com/).
This acknowledgement is retained from the AWS SDK for .NET NOTICE.

This product includes software developed at The Apache Software Foundation
(http://www.apache.org/).

MinIO es una marca de MinIO, Inc. Elcano es un cliente independiente compatible
con MinIO y la API S3; no está afiliado, patrocinado ni respaldado por MinIO, Inc.
El servidor MinIO y su cliente de línea de comandos no forman parte del portable.

Conserva `LICENSE`, este archivo y toda la carpeta `licenses/` al redistribuir
la aplicación, además de cumplir las condiciones de cada componente.
