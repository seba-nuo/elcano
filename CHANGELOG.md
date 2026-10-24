# Historial de cambios

## 0.2.1

- Nuevo nombre **Elcano**, inspirado en Juan Sebastián Elcano: interfaz, ejecutable, solución, proyectos, scripts y paquetes portables actualizados.
- Importación de conexiones y preferencias de la versión anterior para el mismo usuario de Windows, conservando los originales y sin sobrescribir la configuración nueva.
- Descripción de la aplicación como cliente independiente compatible con MinIO y S3.
- Licencia MIT para el código propio y documentos de licencia y atribución para las dependencias .NET y nativas.
- Verificación de versiones, binarios nativos y documentos legales antes de generar el ZIP portable.

## 0.2.0

- Caché limitada de carpetas, páginas y recuentos, con precarga gradual y opción para desactivarla.
- Restauración de página, filtro, selección y posición al volver a una carpeta; filtros nuevos limpios y acción para borrarlos.
- Indicador de caché y hora de actualización del listado.
- Ordenación de la vista previa respetando tipos numéricos y fechas, incluidos enteros y decimales de alta precisión.
- Autocompletado correcto de columnas entre comillas con espacios, guiones y comillas escapadas.
- Selector de hasta 64 columnas del resultado, indicadores de límites y recortes y representación diferenciada de NULL.
- Encabezados que conservan los guiones bajos de los nombres originales.
- Diagnósticos sin mensajes SQL ni credenciales y opción para copiar la referencia de un fallo.
- Código del estado de la ventana organizado por responsabilidad y acceso S3 inyectable para pruebas.
- Pruebas de interfaz WPF mantenidas en el repositorio y flujo de compilación y pruebas en Windows.
- Generación de ZIP portable completo y suma SHA-256.
