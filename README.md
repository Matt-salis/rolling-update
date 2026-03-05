# Rolling Update Manager

Aplicación de escritorio Windows (.NET 8 / WPF) para gestionar múltiples servicios JAR (Spring Boot u otros) con estrategia **Blue/Green** sin downtime, funcionando como alternativa liviana a Docker en un VPS Windows.

---

## 🏗️ Arquitectura

```
RollingUpdateManager/
├── Models/
│   └── Models.cs               ← ServiceConfig, ServiceInstance, ServiceRuntimeState,
│                                  DeploymentRecord, AppData, LogEntry, enums
├── Services/
│   ├── PersistenceService.cs   ← JSON atómico (write-temp → rename)
│   ├── PortManager.cs          ← Gestión de puertos internos dinámicos
│   ├── ProcessLauncher.cs      ← Lanza java.exe, captura stdout/stderr
│   ├── HealthCheckService.cs   ← HTTP /actuator/health + TCP port-open fallback
│   └── ServiceOrchestrator.cs  ← Núcleo: Start, Stop, Restart, Rolling Update
├── Proxy/
│   └── ProxyManager.cs         ← Reverse proxy Kestrel embebido, target dinámico
├── Infrastructure/
│   └── WindowsServiceHost.cs   ← BackgroundService + helpers sc.exe
├── ViewModels/
│   └── ViewModels.cs           ← MainViewModel, ServiceItemViewModel (CommunityToolkit.Mvvm)
├── Views/
│   ├── MainWindow.xaml/cs      ← Ventana principal, lista + panel de logs
│   └── AddEditServiceDialog.xaml/cs ← Diálogo agregar/editar servicio
├── Converters/
│   └── Converters.cs           ← Status→Color, Slot→Color, Log→Color, etc.
├── App.xaml/cs                 ← DI container, modos: GUI / --service / --install
└── RollingUpdateManager.csproj
```

---

## ⚙️ Tecnologías

| Componente       | Tecnología                                   | Razón                                 |
| ---------------- | -------------------------------------------- | ------------------------------------- |
| UI               | WPF (.NET 8)                                 | Nativo Windows, MVVM maduro           |
| MVVM             | CommunityToolkit.Mvvm                        | Source generators, ObservableProperty |
| Proxy            | Kestrel embebido (ASP.NET Core)              | Sin proceso externo, target dinámico  |
| Tema             | MaterialDesignThemes                         | UI moderna dark                       |
| Persistencia     | System.Text.Json (atómico)                   | Sin dependencia externa               |
| Servicio Windows | Microsoft.Extensions.Hosting.WindowsServices | Integración nativa                    |

---

## 🔵🟢 Flujo de Rolling Update

```
Estado inicial:  BLUE activo (puerto interno 10001)
                 Proxy público :8080 → localhost:10001

Paso 1: Arrancar GREEN con nuevo JAR → puerto interno 10002
Paso 2: Esperar health-check OK (HTTP /actuator/health → "UP")
Paso 3: Proxy :8080 → localhost:10002  (cambio atómico, sin downtime)
Paso 4: Drain delay 3s (conexiones activas terminan)
Paso 5: Matar proceso BLUE (puerto 10001 liberado)

Estado final:    GREEN activo (puerto interno 10002)
                 BLUE = libre para próximo update
```

**Si falla en cualquier paso:**

- GREEN es apagado y su puerto liberado
- Proxy continúa apuntando a BLUE
- Error visible en la UI y log

---

## 🚀 Compilar y ejecutar

### Requisitos

- .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8
- Windows 10/11

### Compilar

```bash
cd "d:\rolling update"
dotnet build RollingUpdateManager.sln -c Release
```

### Ejecutar en modo GUI

```bash
dotnet run --project RollingUpdateManager
```

### Instalar como servicio Windows (requiere administrador)

```bash
# Compilar primero
dotnet publish RollingUpdateManager -c Release -r win-x64 --self-contained -o publish/

# Instalar
publish\RollingUpdateManager.exe --install

# Desinstalar
publish\RollingUpdateManager.exe --uninstall
```

### O usar NSSM (recomendado para servicios)

```bash
nssm install RollingUpdateManager "C:\ruta\RollingUpdateManager.exe" "--service"
nssm set RollingUpdateManager AppDirectory "C:\ruta\"
nssm start RollingUpdateManager
```

---

## 📋 Configuración de un servicio

| Campo          | Descripción                       | Ejemplo                   |
| -------------- | --------------------------------- | ------------------------- |
| Nombre         | Nombre visible en la UI           | `API Gateway`             |
| JAR            | Ruta al archivo .jar              | `C:\apps\gateway.jar`     |
| Config         | Ruta a .properties/.yml           | `C:\apps\application.yml` |
| Puerto público | Puerto fijo expuesto              | `8080`                    |
| JVM Args       | Argumentos extra                  | `-Xmx512m -Xms256m`       |
| Health path    | Endpoint de salud                 | `/actuator/health`        |
| Timeout        | Segundos para health-check        | `60`                      |
| Drain delay    | Ms antes de matar instancia vieja | `3000`                    |
| Auto-start     | Arranca con la app                | `true`                    |

---

## 📁 Datos persistidos

Ubicación: `%APPDATA%\RollingUpdateManager\Data\services.json`

```json
{
  "Services": [
    {
      "Id": "guid...",
      "Name": "Mi API",
      "JarPath": "C:\\apps\\api.jar",
      "PublicPort": 8080,
      "ActiveSlot": "Blue",
      "DeploymentHistory": [...]
    }
  ],
  "PortRanges": { "RangeStart": 10000, "RangeEnd": 19999 }
}
```

---

## 🔧 Rango de puertos internos

Por defecto: `10000–19999`. Cada instancia Blue/Green usa un puerto del rango.
Configurable en el JSON de datos directamente.

---

## 🏥 Health Check

Orden de intentos:

1. `GET http://localhost:{internalPort}/actuator/health` → JSON `{"status":"UP"}`
2. Si no responde, fallback a TCP port-open
3. Timeout configurable por servicio

---

## 🪟 Instalación automática con Windows

1. **Como servicio**: ver sección arriba (`sc create` o NSSM)
2. **Inicio de sesión** (GUI): Atajo en `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup`
