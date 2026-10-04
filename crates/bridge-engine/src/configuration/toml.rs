use super::{ConfigurationFailure as Failure, ConfigurationResult, MAX_DOCUMENT_BYTES};
use bridge_toml::{TomlClient, TomlError, TomlPath, TomlReply, TomlRequest, TomlSnapshot};

/// Offline canonical codec only. All mutations preserve producer source spans.
pub trait TomlPreparation {
    fn validate(&mut self, text: &str) -> ConfigurationResult<()>;
    fn read(&mut self, text: &str) -> ConfigurationResult<TomlSnapshot>;
    fn normalize(&mut self, value: &str) -> ConfigurationResult<String>;
    fn set(&mut self, text: &str, path: &TomlPath, value: &str) -> ConfigurationResult<String>;
    fn remove(&mut self, text: &str, path: &TomlPath) -> ConfigurationResult<String>;
    fn remove_table(&mut self, text: &str, path: &TomlPath) -> ConfigurationResult<String>;
    fn rename_table(
        &mut self,
        text: &str,
        path: &TomlPath,
        destination: &TomlPath,
    ) -> ConfigurationResult<String>;
}
pub struct CanonicalToml(pub TomlClient);
fn failure(error: TomlError) -> Failure {
    match error {
        TomlError::NativeRefusal {
            code: bridge_toml::NativeErrorCode::UnsupportedTarget,
            ..
        } => Failure::UnsupportedSyntax,
        TomlError::NativeRefusal { .. } => Failure::InvalidDocument,
        TomlError::RequestTooLarge => Failure::Capacity,
        _ => Failure::NativeUnavailable,
    }
}
impl CanonicalToml {
    fn execute(&mut self, request: TomlRequest<'_>) -> ConfigurationResult<TomlReply> {
        self.0.execute(request).map_err(failure)
    }
    fn edited(&mut self, request: TomlRequest<'_>) -> ConfigurationResult<String> {
        match self.execute(request)? {
            TomlReply::EditedText(value) if value.len() <= MAX_DOCUMENT_BYTES => Ok(value),
            _ => Err(Failure::InvalidOwnerResult),
        }
    }
}
impl TomlPreparation for CanonicalToml {
    fn validate(&mut self, text: &str) -> ConfigurationResult<()> {
        match self.execute(TomlRequest::Validate { text })? {
            TomlReply::Validated => Ok(()),
            _ => Err(Failure::InvalidOwnerResult),
        }
    }
    fn read(&mut self, text: &str) -> ConfigurationResult<TomlSnapshot> {
        match self.execute(TomlRequest::Read { text })? {
            TomlReply::Snapshot(value) => Ok(value),
            _ => Err(Failure::InvalidOwnerResult),
        }
    }
    fn normalize(&mut self, value: &str) -> ConfigurationResult<String> {
        match self.execute(TomlRequest::NormalizeValue { value })? {
            TomlReply::Value(value) => Ok(value),
            _ => Err(Failure::InvalidOwnerResult),
        }
    }
    fn set(&mut self, text: &str, path: &TomlPath, value: &str) -> ConfigurationResult<String> {
        self.edited(TomlRequest::Set { text, path, value })
    }
    fn remove(&mut self, text: &str, path: &TomlPath) -> ConfigurationResult<String> {
        self.edited(TomlRequest::Remove { text, path })
    }
    fn remove_table(&mut self, text: &str, path: &TomlPath) -> ConfigurationResult<String> {
        self.edited(TomlRequest::RemoveTable { text, path })
    }
    fn rename_table(
        &mut self,
        text: &str,
        path: &TomlPath,
        destination: &TomlPath,
    ) -> ConfigurationResult<String> {
        self.edited(TomlRequest::RenameTable {
            text,
            path,
            destination,
        })
    }
}
