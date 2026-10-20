use super::{CapturedOperation, KernelFailure, ResourceKey};
use bridge_contracts::v1::*;

pub(super) fn validate_capture(
    intent: &MutationIntent,
    capture: &CapturedOperation,
) -> Result<(), KernelFailure> {
    let bytes = serde_json::to_vec(intent).map_err(|_| KernelFailure::InvalidPortResult)?;
    let input: serde_json::Value =
        serde_json::from_slice(&bytes).map_err(|_| KernelFailure::InvalidPortResult)?;
    let captured = serde_json::to_value(&capture.semantics.capture)
        .map_err(|_| KernelFailure::InvalidPortResult)?;
    if input["kind"] != captured["kind"] {
        return Err(KernelFailure::InvalidPortResult);
    }
    let matches = match (intent, &capture.semantics.capture) {
        (
            MutationIntent::LaunchOrdinary(i),
            PreparedCapture::LaunchOrdinary {
                target,
                unrecognized_runtime_choice,
                ..
            },
        ) => {
            ordinary_matches(&i.target.profile, &target.profile)
                && installation_matches(&i.target.installation, &target.installation)
                && &i.unrecognized_runtime_choice == unrecognized_runtime_choice
        }
        (
            MutationIntent::LaunchIsolated(i),
            PreparedCapture::LaunchIsolated {
                target,
                store_mode,
                unrecognized_runtime_choice,
                ..
            },
        ) => {
            let IsolatedProfileSelector::Isolated {
                id,
                revision_assertion,
            } = &i.target.profile;
            matches!(&target.profile, ProfileBinding::Isolated { id: captured, revision } if captured == id && revision_assertion.as_ref().is_none_or(|expected| expected == revision))
                && installation_matches(&i.target.installation, &target.installation)
                && &i.store_mode == store_mode
                && &i.unrecognized_runtime_choice == unrecognized_runtime_choice
        }
        (MutationIntent::FocusSession(i), PreparedCapture::FocusSession { session, .. }) => {
            &i.session == session
        }
        (MutationIntent::CreateProfile(i), PreparedCapture::CreateProfile { input: c }) => {
            let RegisteredInstallationSelector::Registered {
                id,
                revision_assertion,
                ..
            } = &i.preferred_installation;
            let RegisteredInstallationBinding::Registered {
                registration_id,
                registration_revision,
                ..
            } = &c.preferred_installation;
            i.name == c.name
                && i.setup == c.setup
                && i.expected_catalog_revision == c.catalog_revision
                && id == registration_id
                && revision_assertion
                    .as_ref()
                    .is_none_or(|r| r == registration_revision)
        }
        (
            MutationIntent::RegisterInstallation(i),
            PreparedCapture::RegisterInstallation { input: c },
        ) => i.name == c.name && i.expected_catalog_revision == c.catalog_revision,
        (MutationIntent::SaveConfiguration(i), PreparedCapture::SaveConfiguration { input: c }) => {
            i.draft == c.draft.draft
        }
        _ => input["input"] == captured["input"],
    };
    if !matches {
        return Err(KernelFailure::InvalidPortResult);
    }
    // Native path assertions and directory-to-physical resolution are checked by
    // capture and then again under the canonical exclusion in revalidate.
    if capture.resources.is_empty()
        || capture.resources.len() > 16
        || !super::resources_unique(&capture.resources)
    {
        return Err(KernelFailure::InvalidPortResult);
    }
    Ok(())
}

fn ordinary_matches(selector: &OrdinaryProfileSelector, binding: &ProfileBinding) -> bool {
    let OrdinaryProfileSelector::Ordinary {
        catalog_id_assertion,
    } = selector;
    matches!(binding, ProfileBinding::Ordinary { ordinary_id, .. } if catalog_id_assertion.as_ref().is_none_or(|id| Some(id) == ordinary_id.as_ref()))
}

fn installation_matches(selector: &InstallationSelector, binding: &InstallationBinding) -> bool {
    match (selector, binding) {
        (
            InstallationSelector::Registered {
                id,
                revision_assertion,
                ..
            },
            InstallationBinding::Registered {
                registration_id,
                registration_revision,
                ..
            },
        ) => {
            id == registration_id
                && revision_assertion
                    .as_ref()
                    .is_none_or(|r| r == registration_revision)
        }
        // A directory may name an already registered installation. The native
        // owner resolves its physical identity during capture and rechecks that
        // exact binding under the retained owner exclusion during admission.
        (InstallationSelector::Directory { .. }, _) => true,
        _ => false,
    }
}

/// This is a minimum resource set; an owner may require additional exclusions.
/// Different document IDs still share their physical installation exclusion.
pub(super) fn required_resources(capture: &PreparedCapture) -> Vec<ResourceKey> {
    fn target(t: &ResolvedTarget) -> Vec<ResourceKey> {
        let mut result = vec![ResourceKey::Installation {
            physical_id: t.installation.physical_id().clone(),
        }];
        result.push(match &t.profile {
            ProfileBinding::Ordinary { owner_scope, .. } => ResourceKey::OrdinaryProfile {
                owner: owner_scope.clone(),
            },
            ProfileBinding::Isolated { id, .. } => ResourceKey::IsolatedProfile { id: id.clone() },
        });
        result
    }
    fn document(d: &DocumentBinding) -> Vec<ResourceKey> {
        let mut result = target(&d.target);
        result.push(ResourceKey::Document {
            id: d.document_id.clone(),
            target: d.target.clone(),
        });
        result
    }
    match capture {
        PreparedCapture::LaunchOrdinary { target: t, .. }
        | PreparedCapture::LaunchIsolated { target: t, .. }
        | PreparedCapture::FocusSession { target: t, .. } => target(t),
        PreparedCapture::CreateProfile { input } => vec![
            ResourceKey::Catalog {
                owner: input.destination_owner.clone(),
            },
            ResourceKey::Installation {
                physical_id: match &input.preferred_installation {
                    RegisteredInstallationBinding::Registered { physical_id, .. } => {
                        physical_id.clone()
                    }
                },
            },
        ],
        PreparedCapture::EditOrdinaryProfile { input } => vec![ResourceKey::OrdinaryProfile {
            owner: match &input.profile {
                OrdinaryProfileRef::Ordinary { owner_scope, .. } => owner_scope.clone(),
            },
        }],
        PreparedCapture::EditIsolatedProfile { input } => vec![ResourceKey::IsolatedProfile {
            id: input.profile.id.clone(),
        }],
        PreparedCapture::ArchiveProfile { input } | PreparedCapture::RestoreProfile { input } => {
            vec![ResourceKey::IsolatedProfile {
                id: input.profile.id.clone(),
            }]
        }
        PreparedCapture::DeleteProfile { input } => vec![ResourceKey::IsolatedProfile {
            id: input.profile.id.clone(),
        }],
        PreparedCapture::RegisterInstallation { input } => vec![ResourceKey::Installation {
            physical_id: input.physical_id.clone(),
        }],
        PreparedCapture::EditInstallation { input } => vec![ResourceKey::Installation {
            physical_id: input.installation.physical_id().clone(),
        }],
        PreparedCapture::SaveConfiguration { input } => document(&input.draft.draft.document),
        PreparedCapture::RestoreConfiguration { input } => document(&input.document),
        PreparedCapture::RuntimeInstall { input, .. }
        | PreparedCapture::RuntimeUpdate { input, .. }
        | PreparedCapture::RuntimeRepair { input, .. } => target(&input.target),
        PreparedCapture::RuntimeAdopt { input } => target(&input.target),
        PreparedCapture::RuntimeRemove { input }
        | PreparedCapture::RuntimeStopManaging { input } => target(&input.reference.target),
        PreparedCapture::RuntimeSwitchSource { input, .. } => target(&input.current.target),
        PreparedCapture::GameUpdate { input } => vec![ResourceKey::Installation {
            physical_id: input.checked_update.installation.physical_id().clone(),
        }],
        PreparedCapture::RecoverGameUpdate { input } => vec![ResourceKey::Installation {
            physical_id: input.recovery.installation.physical_id().clone(),
        }],
        PreparedCapture::BridgeUpdate { input } => vec![ResourceKey::Application {
            identity: input.selected_release.current.application_id.clone(),
        }],
        PreparedCapture::RecoverBridgeUpdate { input } => vec![ResourceKey::Application {
            identity: input.recovery.application.application_id.clone(),
        }],
        PreparedCapture::ExportDiagnostics { input } => vec![ResourceKey::Export {
            destination: input.destination.destination_id.clone(),
        }],
        // Preferences do not carry an application identity in v1. A canonical
        // owner scoped application-state exclusion must be supplied by the port.
        PreparedCapture::SaveApplicationPreferences { .. } => vec![],
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::{collections::BTreeMap, path::Path};

    fn fixture_bytes(root: &Path, descriptor: &serde_json::Value) -> Vec<u8> {
        let name = descriptor["path"].as_str().unwrap();
        let relative = Path::new(name);
        assert_eq!(relative.file_name().unwrap(), relative.as_os_str());
        std::fs::read(root.join(relative)).unwrap()
    }

    #[test]
    fn accepted_fixture_preparations_match_captures_with_valid_owner_resources() {
        let root = Path::new(env!("CARGO_MANIFEST_DIR")).join("../../contracts/fixtures");
        let index: serde_json::Value =
            serde_json::from_slice(&std::fs::read(root.join("index.json")).unwrap()).unwrap();
        let fixtures: BTreeMap<_, _> = index["fixtures"]
            .as_array()
            .unwrap()
            .iter()
            .map(|fixture| (fixture["id"].as_str().unwrap(), fixture))
            .collect();
        let mut checked = 0;
        let mut actions = std::collections::BTreeSet::new();
        for descriptor in index["transcripts"].as_array().unwrap() {
            if descriptor["expected"]["accepted"] != true {
                continue;
            }
            let transcript: serde_json::Value =
                serde_json::from_slice(&fixture_bytes(&root, descriptor)).unwrap();
            for step in transcript["steps"].as_array().unwrap() {
                if step["type"] != "exchange" {
                    continue;
                }
                let request = decode_request(&fixture_bytes(
                    &root,
                    fixtures[step["request"].as_str().unwrap()],
                ))
                .unwrap()
                .into_inner();
                let RequestBody::Command {
                    command: Command::Prepare(input),
                } = request.body
                else {
                    continue;
                };
                let reply = decode_reply(&fixture_bytes(
                    &root,
                    fixtures[step["reply"].as_str().unwrap()],
                ))
                .unwrap()
                .into_inner();
                let ReplyBody::Result {
                    result: ResultPayload::Command { command },
                } = reply.body
                else {
                    continue;
                };
                let CommandResult::Prepare(plan) = *command else {
                    panic!("accepted prepare exchange returned another command result");
                };
                let mut resources = required_resources(&plan.semantics.capture);
                if resources.is_empty() {
                    // v1 preferences rely on the port's application-state owner.
                    resources.push(ResourceKey::Application {
                        identity: ApplicationIdentity::new("fixture-parity-owner").unwrap(),
                    });
                }
                let mut capture = CapturedOperation {
                    semantics: plan.semantics,
                    resources,
                };
                assert!(
                    validate_capture(&input.intent, &capture).is_ok(),
                    "accepted corpus prepare binding failed: {} {}",
                    descriptor["id"],
                    step["request"]
                );
                actions.insert(serde_json::to_string(&capture.semantics.action).unwrap());
                checked += 1;
                // Legal intent/capture pairs never waive owner exclusion shape.
                let resources = std::mem::take(&mut capture.resources);
                assert!(matches!(
                    validate_capture(&input.intent, &capture),
                    Err(KernelFailure::InvalidPortResult)
                ));
                capture.resources = resources;
                capture.resources.push(capture.resources[0].clone());
                assert!(matches!(
                    validate_capture(&input.intent, &capture),
                    Err(KernelFailure::InvalidPortResult)
                ));
            }
        }
        assert!(checked >= 26, "accepted preparation coverage disappeared");
        assert_eq!(actions.len(), 26, "the corpus must cover every v1 action");
    }
}
