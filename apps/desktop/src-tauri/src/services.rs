//! Cross-platform service control. Windows uses the SCM via the `windows-service` crate.
//! Linux/macOS implementations are stubs for now (Phase 2 ships Windows-first; the cross-
//! platform follow-up is on the plan's open-questions list).

use serde::{Deserialize, Serialize};

use crate::DesktopError;

#[derive(Debug, Serialize, Deserialize, Clone, Copy, PartialEq, Eq)]
#[serde(rename_all = "lowercase")]
pub enum ServiceName {
    Api,
    Ingestor,
}

impl ServiceName {
    fn windows_name(self) -> &'static str {
        match self {
            ServiceName::Api => "aimemory-api",
            ServiceName::Ingestor => "aimemory-ingestor",
        }
    }
}

#[derive(Debug, Serialize, Clone)]
#[serde(rename_all = "lowercase")]
pub enum ServiceState {
    Running,
    Stopped,
    StartPending,
    StopPending,
    NotInstalled,
    Unknown,
}

#[derive(Debug, Serialize, Clone)]
pub struct ServiceStatus {
    pub name: ServiceName,
    pub state: ServiceState,
    /// Process ID when running, otherwise None.
    pub pid: Option<u32>,
    /// Display name from the service registration, useful for surfacing to users
    /// (e.g. "AIMemory API" instead of "aimemory-api").
    #[serde(rename = "displayName")]
    pub display_name: Option<String>,
}

#[cfg(windows)]
mod platform {
    use super::*;
    use windows_service::service::{
        ServiceAccess, ServiceState as WinState,
    };
    use windows_service::service_manager::{ServiceManager, ServiceManagerAccess};

    fn manager() -> Result<ServiceManager, DesktopError> {
        // CONNECT is sufficient for query/start/stop. We never create services from the
        // running app — the installer (Phase 2.6) does that with full admin access.
        ServiceManager::local_computer(None::<&str>, ServiceManagerAccess::CONNECT)
            .map_err(|e| DesktopError::Service(format!("opening SCM: {e}")))
    }

    pub fn status(name: ServiceName) -> Result<ServiceStatus, DesktopError> {
        let manager = manager()?;

        let access = ServiceAccess::QUERY_STATUS | ServiceAccess::QUERY_CONFIG;
        let service = match manager.open_service(name.windows_name(), access) {
            Ok(s) => s,
            Err(windows_service::Error::Winapi(e))
                if e.raw_os_error() == Some(windows_service::sc_error::ERROR_SERVICE_DOES_NOT_EXIST as i32) =>
            {
                return Ok(ServiceStatus {
                    name,
                    state: ServiceState::NotInstalled,
                    pid: None,
                    display_name: None,
                });
            }
            Err(e) => return Err(DesktopError::Service(format!("opening service: {e}"))),
        };

        let info = service.query_status()
            .map_err(|e| DesktopError::Service(format!("query_status: {e}")))?;

        let state = match info.current_state {
            WinState::Running => ServiceState::Running,
            WinState::Stopped => ServiceState::Stopped,
            WinState::StartPending => ServiceState::StartPending,
            WinState::StopPending => ServiceState::StopPending,
            _ => ServiceState::Unknown,
        };

        let pid = info.process_id;
        let display_name = service.query_config()
            .ok()
            .map(|c| c.display_name);

        Ok(ServiceStatus { name, state, pid, display_name })
    }

    pub fn start(name: ServiceName) -> Result<(), DesktopError> {
        let manager = manager()?;
        let service = manager
            .open_service(name.windows_name(), ServiceAccess::START | ServiceAccess::QUERY_STATUS)
            .map_err(|e| DesktopError::Service(format!("opening for start: {e}")))?;

        // start_with_args is &[&OsStr]; empty slice means default config.
        service.start::<&str>(&[])
            .map_err(|e| DesktopError::Service(format!("start: {e}")))?;
        Ok(())
    }

    pub fn stop(name: ServiceName) -> Result<(), DesktopError> {
        let manager = manager()?;
        let service = manager
            .open_service(name.windows_name(), ServiceAccess::STOP | ServiceAccess::QUERY_STATUS)
            .map_err(|e| DesktopError::Service(format!("opening for stop: {e}")))?;

        service.stop()
            .map_err(|e| DesktopError::Service(format!("stop: {e}")))?;
        Ok(())
    }
}

#[cfg(not(windows))]
mod platform {
    use super::*;

    pub fn status(name: ServiceName) -> Result<ServiceStatus, DesktopError> {
        // TODO Phase 2.x: shell out to `systemctl --user is-active aimemory-api` etc.
        Ok(ServiceStatus {
            name,
            state: ServiceState::Unknown,
            pid: None,
            display_name: None,
        })
    }

    pub fn start(_name: ServiceName) -> Result<(), DesktopError> {
        Err(DesktopError::Unsupported)
    }

    pub fn stop(_name: ServiceName) -> Result<(), DesktopError> {
        Err(DesktopError::Unsupported)
    }
}

pub fn status(name: ServiceName) -> Result<ServiceStatus, DesktopError> {
    platform::status(name)
}

pub fn start(name: ServiceName) -> Result<(), DesktopError> {
    platform::start(name)
}

pub fn stop(name: ServiceName) -> Result<(), DesktopError> {
    platform::stop(name)
}
