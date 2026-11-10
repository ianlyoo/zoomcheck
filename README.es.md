# ZoomCheck

Panel web para Windows que coteja un padrón en Excel con los participantes de una reunión de Zoom en vivo, ya sea a través de la API Business Dashboard o mediante un puente complementario de Zoom App adecuado para cuentas Pro.

[English](README.md) · [한국어](README.ko.md) · [Guía de operador](docs/windows-operator-guide.md) · [Licencia MIT](LICENSE)

[![CI](https://github.com/ianlyoo/zoomcheck/actions/workflows/ci.yml/badge.svg)](https://github.com/ianlyoo/zoomcheck/actions/workflows/ci.yml)
[![Windows build](https://github.com/ianlyoo/zoomcheck/actions/workflows/build-windows-installer.yml/badge.svg)](https://github.com/ianlyoo/zoomcheck/actions/workflows/build-windows-installer.yml)
[![Latest release](https://img.shields.io/github/v/release/ianlyoo/zoomcheck)](https://github.com/ianlyoo/zoomcheck/releases/latest)

ZoomCheck no realiza scraping de la ventana ni automatiza la interfaz gráfica de Zoom. Las cuentas Business pueden utilizar la API oficial de Dashboard; las implementaciones Pro pueden usar el Zoom Apps SDK oficial desde una aplicación integrada operada por el anfitrión o coanfitrión. Ambas vías alimentan el mismo motor local de conciliación y asistencia.

## Características principales

- **Soporte multi-idioma**: Interfaz disponible en coreano (por defecto), español e inglés con selector rápido en la barra superior y en configuración.
- **Carga de padrón Excel flexible**: Soporta archivos `.xlsx` y `.xls` con encabezados en español (`Nombre`, `DNI`/`Legajo`, `Correo`, `Teléfono`, `Institución`), inglés o coreano.
- **Columnas de comisión o grupo**: Admite agrupaciones (`Comisión`, `Grupo`, `Equipo`, `조`, `분반`, `Team`, `Class`) con totales filtrados, búsqueda y exportación CSV.
- **Sincronización en vivo**: Consulta manual instantánea o sincronización automática configurable entre 5 y 600 segundos.
- **Coincidencia inteligente**: Prioridad por email, nombre, alias y niveles de confianza claros (Verificado, Alias, Solo nombre, Posible, No emparejado).
- **Historial y bitácora persistente**: Registro local en SQLite de ingresos, egresos y cambios de nombre en tiempo real.
- **Filas expandibles de participantes**: Visualización de conexiones actuales, nombre original en Zoom, teléfono, comisión e historial de ingresos/egresos.
- **Detección de conexiones duplicadas**: Alerta cuando un mismo participante se conecta desde múltiples dispositivos.
- **Exclusiones reversibles y revisión en reunión**: Exclusión diaria de ausentes justificados y asociación manual de conexiones desconocidas.
- **Exportación a CSV**: Descarga rápida del estado final de asistencia con soporte de comisiones.
- **Instalador autónomo para Windows**: No requiere instalación previa de .NET.
- **Tema claro y oscuro**: Sincronización con las preferencias de Windows y personalización manual guardada en el equipo.
- **Seguridad y privacidad local**: Servidor local exclusivo en `http://127.0.0.1:5078`; base de datos bajo `%LOCALAPPDATA%\ZoomCheck`.

## Modos de conexión

1. **Automático (recomendado)**: Selecciona la mejor vía disponible según el estado de la reunión.
2. **Business Dashboard API**: Conexión directa mediante Server-to-Server OAuth con granular scope `dashboard:read:list_meeting_participants:admin`.
3. **Zoom App (Pro)**: Comunicación mediante un relay cifrado de punta a punta (E2E con ECDH P-256 + AES-256-GCM) sin compartir credenciales de reunión con intermediarios.
4. **Entrada manual**: Posibilidad de pegar la lista completa de asistentes en texto plano en caso de contingencia o corte de API.

## Desarrollo y pruebas

```bash
# Pruebas de regresión del panel web (Node.js)
node --test tests/dashboard-regressions.cjs

# Pruebas de la solución .NET (requiere SDK 8.0)
dotnet test ZoomCheck.sln -c Release
```
