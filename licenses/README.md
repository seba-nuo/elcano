# Mantenimiento de licencias

Los textos de esta carpeta se conservan desde los paquetes o repositorios
oficiales de las dependencias. `manifest.json` registra las versiones revisadas,
las fuentes y las sumas SHA-256. Los avisos internos de DuckDB se conservan
también para las bibliotecas enlazadas en su extensión httpfs.

Al actualizar una dependencia:

1. Comprueba la licencia de la versión nueva y las de sus componentes nativos.
2. Conserva sus textos originales de licencia y avisos de atribución, incluidos
   los archivos NOTICE y los avisos de los componentes vendorizados.
3. Actualiza los documentos, versiones y sumas de `manifest.json`, junto con
   el inventario de `THIRD-PARTY-NOTICES.md`. No actualices una suma para eludir
   la revisión de un cambio.
4. Si cambia .NET, actualiza también `RuntimeFrameworkVersion` y los avisos
   correspondientes del runtime y Windows Desktop/WPF.
5. Ejecuta `scripts/publish.ps1`. La publicación comprueba que las dependencias,
   binarios nativos y documentos del portable coincidan con el inventario.

La verificación no descarga documentos durante la publicación ni sustituye
la revisión de las condiciones de una dependencia nueva.
