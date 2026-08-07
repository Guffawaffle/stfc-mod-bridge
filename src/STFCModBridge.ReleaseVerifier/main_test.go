package main

import (
	"bytes"
	"encoding/base64"
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

const fixtureTag = "v0.1.0-rc.4"

func TestCapturedBroadReleaseBundlePassesCryptographyThenFailsClosedOnSubjectCardinality(t *testing.T) {
	requestJSON := fixtureRequest(t, fixtureTag)
	var output bytes.Buffer

	err := run(strings.NewReader(requestJSON), &output)

	if err == nil || !strings.Contains(err.Error(), "exactly one statement subject") {
		t.Fatalf("expected closed subject-cardinality rejection, got output %q and error %v", output.String(), err)
	}
}

func TestTamperedManifestFailsSigstoreVerification(t *testing.T) {
	directory := t.TempDir()
	manifestPath := filepath.Join(directory, manifestName)
	bundlePath := filepath.Join(directory, bundleName)
	copyFixture(t, manifestName, manifestPath)
	copyFixture(t, bundleName, bundlePath)
	file, err := os.OpenFile(manifestPath, os.O_APPEND|os.O_WRONLY, 0)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := file.WriteString(" "); err != nil {
		t.Fatal(err)
	}
	if err := file.Close(); err != nil {
		t.Fatal(err)
	}
	requestBytes, err := json.Marshal(request{
		SchemaVersion: requestSchemaVersion,
		ManifestPath:  manifestPath,
		BundlePath:    bundlePath,
		ExpectedTag:   fixtureTag,
	})
	if err != nil {
		t.Fatal(err)
	}

	err = run(bytes.NewReader(requestBytes), &bytes.Buffer{})

	if err == nil || !strings.Contains(err.Error(), "Sigstore verification") {
		t.Fatalf("expected cryptographic rejection, got %v", err)
	}
}

func TestMutatedSignedClaimsFailClosed(t *testing.T) {
	tests := map[string]func(map[string]any){
		"repository": func(statement map[string]any) {
			workflow(statement)["repository"] = "https://github.com/attacker/repository"
		},
		"workflow": func(statement map[string]any) {
			workflow(statement)["path"] = ".github/workflows/other.yml"
		},
		"ref": func(statement map[string]any) {
			workflow(statement)["ref"] = "refs/tags/v9.9.9"
		},
		"repository id": func(statement map[string]any) {
			githubParameters(statement)["repository_id"] = "1"
		},
		"owner id": func(statement map[string]any) {
			githubParameters(statement)["repository_owner_id"] = "1"
		},
		"event": func(statement map[string]any) {
			githubParameters(statement)["event_name"] = "pull_request"
		},
		"runner": func(statement map[string]any) {
			githubParameters(statement)["runner_environment"] = "self-hosted"
		},
		"predicate": func(statement map[string]any) {
			statement["predicateType"] = "https://example.invalid/predicate"
		},
		"missing subject": func(statement map[string]any) {
			statement["subject"] = []any{}
		},
		"wrong subject": func(statement map[string]any) {
			statement["subject"].([]any)[0].(map[string]any)["name"] = "other.json"
		},
		"duplicate subject": func(statement map[string]any) {
			subjects := statement["subject"].([]any)
			statement["subject"] = append(subjects, subjects[0])
		},
		"commit": func(statement map[string]any) {
			dependency := buildDefinition(statement)["resolvedDependencies"].([]any)[0].(map[string]any)
			dependency["digest"].(map[string]any)["gitCommit"] = strings.Repeat("0", 40)
		},
		"invocation": func(statement map[string]any) {
			statement["predicate"].(map[string]any)["runDetails"].(map[string]any)["metadata"].(map[string]any)["invocationId"] = "https://example.invalid/run"
		},
	}
	for name, mutate := range tests {
		t.Run(name, func(t *testing.T) {
			directory := t.TempDir()
			manifestPath := filepath.Join(directory, manifestName)
			bundlePath := filepath.Join(directory, bundleName)
			copyFixture(t, manifestName, manifestPath)
			mutateBundleFixture(t, bundlePath, mutate)
			requestBytes, err := json.Marshal(request{
				SchemaVersion: requestSchemaVersion,
				ManifestPath:  manifestPath,
				BundlePath:    bundlePath,
				ExpectedTag:   fixtureTag,
			})
			if err != nil {
				t.Fatal(err)
			}

			if err := run(bytes.NewReader(requestBytes), &bytes.Buffer{}); err == nil {
				t.Fatal("mutated signed claim was accepted")
			}
		})
	}
}

func TestWrongExpectedRefFailsClosed(t *testing.T) {
	err := run(strings.NewReader(fixtureRequest(t, "v0.1.0-rc.3")), &bytes.Buffer{})

	if err == nil || !strings.Contains(err.Error(), "CertificateIdentity") {
		t.Fatalf("expected exact certificate identity rejection, got %v", err)
	}
}

func TestReadRequestRejectsUnknownFields(t *testing.T) {
	requestJSON := strings.TrimSuffix(fixtureRequest(t, fixtureTag), "}") + `,"allowUnsafe":true}`

	_, err := readRequest(strings.NewReader(requestJSON))

	if err == nil || !strings.Contains(err.Error(), "unknown field") {
		t.Fatalf("expected unknown-field rejection, got %v", err)
	}
}

func TestReadRequestRejectsTrailingJSON(t *testing.T) {
	_, err := readRequest(strings.NewReader(fixtureRequest(t, fixtureTag) + `{}`))

	if err == nil || !strings.Contains(err.Error(), "trailing") {
		t.Fatalf("expected trailing JSON rejection, got %v", err)
	}
}

func TestReadRequestRejectsDuplicateProperties(t *testing.T) {
	requestJSON := strings.TrimSuffix(fixtureRequest(t, fixtureTag), "}") + `,"expectedTag":"v9.9.9"}`

	_, err := readRequest(strings.NewReader(requestJSON))

	if err == nil || !strings.Contains(err.Error(), "duplicate property") {
		t.Fatalf("expected duplicate-property rejection, got %v", err)
	}
}

func TestReadRequestRejectsNonCanonicalTag(t *testing.T) {
	_, err := readRequest(strings.NewReader(fixtureRequest(t, "refs/tags/v0.1.0")))

	if err == nil || !strings.Contains(err.Error(), "not canonical") {
		t.Fatalf("expected tag rejection, got %v", err)
	}
}

func TestReadRequestRejectsOversizedInput(t *testing.T) {
	_, err := readRequest(strings.NewReader(strings.Repeat("x", maxRequestBytes+1)))

	if err == nil || !strings.Contains(err.Error(), "exceeds") {
		t.Fatalf("expected size rejection, got %v", err)
	}
}

func TestOversizedManifestFailsBeforeBundleVerification(t *testing.T) {
	directory := t.TempDir()
	manifestPath := filepath.Join(directory, manifestName)
	bundlePath := filepath.Join(directory, bundleName)
	if err := os.WriteFile(manifestPath, make([]byte, maxManifestBytes+1), 0o600); err != nil {
		t.Fatal(err)
	}
	copyFixture(t, bundleName, bundlePath)
	requestBytes, err := json.Marshal(request{
		SchemaVersion: requestSchemaVersion,
		ManifestPath:  manifestPath,
		BundlePath:    bundlePath,
		ExpectedTag:   fixtureTag,
	})
	if err != nil {
		t.Fatal(err)
	}

	err = run(bytes.NewReader(requestBytes), &bytes.Buffer{})

	if err == nil || !strings.Contains(err.Error(), "outside the accepted range") {
		t.Fatalf("expected bounded-file rejection, got %v", err)
	}
}

func fixtureRequest(t *testing.T, tag string) string {
	t.Helper()
	directory := filepath.Join("testdata", "rc4-broad-attestation")
	manifestPath, err := filepath.Abs(filepath.Join(directory, manifestName))
	if err != nil {
		t.Fatal(err)
	}
	bundlePath, err := filepath.Abs(filepath.Join(directory, bundleName))
	if err != nil {
		t.Fatal(err)
	}
	requestBytes, err := json.Marshal(request{
		SchemaVersion: requestSchemaVersion,
		ManifestPath:  manifestPath,
		BundlePath:    bundlePath,
		ExpectedTag:   tag,
	})
	if err != nil {
		t.Fatal(err)
	}
	return string(requestBytes)
}

func copyFixture(t *testing.T, name, target string) {
	t.Helper()
	data, err := os.ReadFile(filepath.Join("testdata", "rc4-broad-attestation", name))
	if err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(target, data, 0o600); err != nil {
		t.Fatal(err)
	}
}

func mutateBundleFixture(t *testing.T, target string, mutate func(map[string]any)) {
	t.Helper()
	bundleBytes, err := os.ReadFile(filepath.Join("testdata", "rc4-broad-attestation", bundleName))
	if err != nil {
		t.Fatal(err)
	}
	var bundleDocument map[string]any
	if err := json.Unmarshal(bundleBytes, &bundleDocument); err != nil {
		t.Fatal(err)
	}
	envelope := bundleDocument["dsseEnvelope"].(map[string]any)
	payload, err := base64.StdEncoding.DecodeString(envelope["payload"].(string))
	if err != nil {
		t.Fatal(err)
	}
	var statement map[string]any
	if err := json.Unmarshal(payload, &statement); err != nil {
		t.Fatal(err)
	}
	mutate(statement)
	payload, err = json.Marshal(statement)
	if err != nil {
		t.Fatal(err)
	}
	envelope["payload"] = base64.StdEncoding.EncodeToString(payload)
	bundleBytes, err = json.Marshal(bundleDocument)
	if err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(target, bundleBytes, 0o600); err != nil {
		t.Fatal(err)
	}
}

func buildDefinition(statement map[string]any) map[string]any {
	return statement["predicate"].(map[string]any)["buildDefinition"].(map[string]any)
}

func workflow(statement map[string]any) map[string]any {
	return buildDefinition(statement)["externalParameters"].(map[string]any)["workflow"].(map[string]any)
}

func githubParameters(statement map[string]any) map[string]any {
	return buildDefinition(statement)["internalParameters"].(map[string]any)["github"].(map[string]any)
}

func FuzzReadRequest(f *testing.F) {
	f.Add([]byte(`{"schemaVersion":1}`))
	f.Add([]byte(`{"schemaVersion":1,"allowUnsafe":true}`))
	f.Fuzz(func(t *testing.T, input []byte) {
		_, _ = readRequest(bytes.NewReader(input))
	})
}
