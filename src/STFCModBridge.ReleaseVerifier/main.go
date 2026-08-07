package main

import (
	"bytes"
	"crypto/sha256"
	"embed"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"time"

	"github.com/sigstore/sigstore-go/pkg/bundle"
	"github.com/sigstore/sigstore-go/pkg/fulcio/certificate"
	"github.com/sigstore/sigstore-go/pkg/root"
	"github.com/sigstore/sigstore-go/pkg/verify"
	"google.golang.org/protobuf/encoding/protojson"
)

const (
	requestSchemaVersion = 1
	receiptSchemaVersion = 1
	maxRequestBytes      = 8 * 1024
	maxManifestBytes     = 1024 * 1024
	maxBundleBytes       = 1024 * 1024
	maxJSONDepth         = 32
	manifestName         = "stfc-mod-bridge-release-manifest.json"
	bundleName           = "stfc-mod-bridge-release-selection-attestation.json"
	repository           = "Guffawaffle/stfc-mod-bridge"
	repositoryURI        = "https://github.com/Guffawaffle/stfc-mod-bridge"
	repositoryID         = "1320037274"
	ownerURI             = "https://github.com/Guffawaffle"
	ownerID              = "105761663"
	workflowPath         = ".github/workflows/release.yml"
	oidcIssuer           = "https://token.actions.githubusercontent.com"
	eventName            = "push"
	runnerEnvironment    = "github-hosted"
	predicateType        = "https://slsa.dev/provenance/v1"
	statementType        = "https://in-toto.io/Statement/v1"
	buildType            = "https://actions.github.io/buildtypes/workflow/v1"
	trustEpoch           = 1
	trustedRootSHA256    = "844a1c6de3986c9f02070266b25e0d1a2fa99ceccc89f6b9ad90aae47b62a16e"
)

var (
	canonicalTag = regexp.MustCompile(`^v[0-9]+\.[0-9]+\.[0-9]+(?:-rc\.[0-9]+)?$`)
	commitSHA    = regexp.MustCompile(`^[0-9a-f]{40}$`)
	runURI       = regexp.MustCompile(`^https://github\.com/Guffawaffle/stfc-mod-bridge/actions/runs/[1-9][0-9]*/attempts/[1-9][0-9]*$`)

	//go:embed trusted-root.public-good.v1.json
	trustAssets embed.FS
)

type request struct {
	SchemaVersion int    `json:"schemaVersion"`
	ManifestPath  string `json:"manifestPath"`
	BundlePath    string `json:"bundlePath"`
	ExpectedTag   string `json:"expectedTag"`
}

type receipt struct {
	SchemaVersion     int          `json:"schemaVersion"`
	Verified          bool         `json:"verified"`
	VerificationMode  string       `json:"verificationMode"`
	Repository        string       `json:"repository"`
	RepositoryID      string       `json:"repositoryId"`
	OwnerID           string       `json:"ownerId"`
	Workflow          string       `json:"workflow"`
	SourceRef         string       `json:"sourceRef"`
	SourceCommit      string       `json:"sourceCommit"`
	Event             string       `json:"event"`
	Runner            string       `json:"runner"`
	StatementType     string       `json:"statementType"`
	PredicateType     string       `json:"predicateType"`
	BuildType         string       `json:"buildType"`
	SubjectName       string       `json:"subjectName"`
	ManifestSHA256    string       `json:"manifestSha256"`
	BundleSHA256      string       `json:"bundleSha256"`
	TrustEpoch        int          `json:"trustEpoch"`
	TrustedRootSHA256 string       `json:"trustedRootSha256"`
	FulcioIssuer      string       `json:"fulcioIssuer"`
	FulcioSAN         string       `json:"fulcioSan"`
	RekorEntries      []rekorEntry `json:"rekorEntries"`
	Checks            []string     `json:"checks"`
}

type rekorEntry struct {
	LogID          string    `json:"logId"`
	LogIndex       int64     `json:"logIndex"`
	IntegratedTime time.Time `json:"integratedTime"`
}

type provenancePredicate struct {
	BuildDefinition struct {
		BuildType          string `json:"buildType"`
		ExternalParameters struct {
			Workflow struct {
				Ref        string `json:"ref"`
				Repository string `json:"repository"`
				Path       string `json:"path"`
			} `json:"workflow"`
		} `json:"externalParameters"`
		InternalParameters struct {
			GitHub struct {
				EventName         string `json:"event_name"`
				RepositoryID      string `json:"repository_id"`
				RepositoryOwnerID string `json:"repository_owner_id"`
				RunnerEnvironment string `json:"runner_environment"`
			} `json:"github"`
		} `json:"internalParameters"`
		ResolvedDependencies []struct {
			URI    string            `json:"uri"`
			Digest map[string]string `json:"digest"`
		} `json:"resolvedDependencies"`
	} `json:"buildDefinition"`
	RunDetails struct {
		Builder struct {
			ID string `json:"id"`
		} `json:"builder"`
		Metadata struct {
			InvocationID string `json:"invocationId"`
		} `json:"metadata"`
	} `json:"runDetails"`
}

func main() {
	if err := run(os.Stdin, os.Stdout); err != nil {
		fmt.Fprintln(os.Stderr, "verification failed:", err)
		os.Exit(1)
	}
}

func run(stdin io.Reader, stdout io.Writer) error {
	req, err := readRequest(stdin)
	if err != nil {
		return err
	}
	manifestBytes, err := readBoundedRegularFile(req.ManifestPath, maxManifestBytes)
	if err != nil {
		return fmt.Errorf("manifest: %w", err)
	}
	bundleBytes, err := readBoundedRegularFile(req.BundlePath, maxBundleBytes)
	if err != nil {
		return fmt.Errorf("bundle: %w", err)
	}
	if err := checkJSONDocument(bundleBytes, maxJSONDepth); err != nil {
		return fmt.Errorf("bundle: %w", err)
	}

	manifestDigest := sha256.Sum256(manifestBytes)
	bundleDigest := sha256.Sum256(bundleBytes)
	rootJSON, err := trustAssets.ReadFile("trusted-root.public-good.v1.json")
	if err != nil {
		return fmt.Errorf("embedded trusted root: %w", err)
	}
	if len(rootJSON) < 2 || rootJSON[len(rootJSON)-1] != '\n' || bytes.Contains(rootJSON[:len(rootJSON)-1], []byte{'\n'}) {
		return errors.New("embedded trusted root is not the reviewed single-line document")
	}
	rootJSON = rootJSON[:len(rootJSON)-1]
	rootDigest := sha256.Sum256(rootJSON)
	if hex.EncodeToString(rootDigest[:]) != trustedRootSHA256 {
		return errors.New("embedded trusted root digest does not match the reviewed trust epoch")
	}

	trustedRoot, err := root.NewTrustedRootFromJSON(rootJSON)
	if err != nil {
		return fmt.Errorf("embedded trusted root: %w", err)
	}
	var signedBundle bundle.Bundle
	if err := signedBundle.UnmarshalJSON(bundleBytes); err != nil {
		return fmt.Errorf("Sigstore bundle: %w", err)
	}
	envelope, err := signedBundle.Envelope()
	if err != nil || envelope.PayloadType != bundle.IntotoMediaType || len(envelope.Signatures) != 1 {
		return errors.New("Sigstore bundle must contain exactly one in-toto DSSE signature")
	}

	expectedRef := "refs/tags/" + req.ExpectedTag
	expectedSigner := repositoryURI + "/" + workflowPath + "@" + expectedRef
	san, err := verify.NewSANMatcher(expectedSigner, "")
	if err != nil {
		return err
	}
	issuer, err := verify.NewIssuerMatcher(oidcIssuer, "")
	if err != nil {
		return err
	}
	identity, err := verify.NewCertificateIdentity(san, issuer, certificate.Extensions{
		BuildSignerURI:                      expectedSigner,
		RunnerEnvironment:                   runnerEnvironment,
		SourceRepositoryURI:                 repositoryURI,
		SourceRepositoryRef:                 expectedRef,
		SourceRepositoryIdentifier:          repositoryID,
		SourceRepositoryOwnerURI:            ownerURI,
		SourceRepositoryOwnerIdentifier:     ownerID,
		BuildConfigURI:                      expectedSigner,
		BuildTrigger:                        eventName,
		SourceRepositoryVisibilityAtSigning: "public",
	})
	if err != nil {
		return fmt.Errorf("closed certificate policy: %w", err)
	}
	verifier, err := verify.NewVerifier(
		trustedRoot,
		verify.WithTransparencyLog(1),
		verify.WithObserverTimestamps(1),
		verify.WithSignedCertificateTimestamps(1),
	)
	if err != nil {
		return fmt.Errorf("verifier: %w", err)
	}
	result, err := verifier.Verify(
		&signedBundle,
		verify.NewPolicy(
			verify.WithArtifactDigest("sha256", manifestDigest[:]),
			verify.WithCertificateIdentity(identity),
		),
	)
	if err != nil {
		return fmt.Errorf("Sigstore verification: %w", err)
	}

	commit, err := validateResult(result, expectedRef, expectedSigner, manifestDigest)
	if err != nil {
		return err
	}
	tlogEntries, err := signedBundle.TlogEntries()
	if err != nil {
		return fmt.Errorf("transparency entries: %w", err)
	}
	if len(tlogEntries) != 1 {
		return fmt.Errorf("expected exactly one Rekor entry, found %d", len(tlogEntries))
	}
	cert := result.Signature.Certificate
	output := receipt{
		SchemaVersion:     receiptSchemaVersion,
		Verified:          true,
		VerificationMode:  "offline",
		Repository:        repository,
		RepositoryID:      repositoryID,
		OwnerID:           ownerID,
		Workflow:          workflowPath,
		SourceRef:         expectedRef,
		SourceCommit:      commit,
		Event:             eventName,
		Runner:            runnerEnvironment,
		StatementType:     statementType,
		PredicateType:     predicateType,
		BuildType:         buildType,
		SubjectName:       manifestName,
		ManifestSHA256:    hex.EncodeToString(manifestDigest[:]),
		BundleSHA256:      hex.EncodeToString(bundleDigest[:]),
		TrustEpoch:        trustEpoch,
		TrustedRootSHA256: trustedRootSHA256,
		FulcioIssuer:      cert.Issuer,
		FulcioSAN:         cert.SubjectAlternativeName,
		RekorEntries: []rekorEntry{{
			LogID:          tlogEntries[0].LogKeyID(),
			LogIndex:       tlogEntries[0].LogIndex(),
			IntegratedTime: tlogEntries[0].IntegratedTime().UTC(),
		}},
		Checks: []string{
			"bundle-signature", "manifest-digest", "fulcio-chain", "certificate-transparency",
			"rekor-inclusion", "repository", "workflow", "tag-ref", "source-commit",
			"event", "runner", "statement", "predicate", "single-subject", "embedded-trust-root",
		},
	}
	encoder := json.NewEncoder(stdout)
	encoder.SetEscapeHTML(false)
	if err := encoder.Encode(output); err != nil {
		return fmt.Errorf("receipt: %w", err)
	}
	return nil
}

func readRequest(input io.Reader) (request, error) {
	limited := io.LimitReader(input, maxRequestBytes+1)
	data, err := io.ReadAll(limited)
	if err != nil {
		return request{}, fmt.Errorf("request: %w", err)
	}
	if len(data) > maxRequestBytes {
		return request{}, errors.New("request exceeds 8192 bytes")
	}
	if err := checkJSONDocument(data, 8); err != nil {
		return request{}, fmt.Errorf("request: %w", err)
	}
	var req request
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&req); err != nil {
		return request{}, fmt.Errorf("request is invalid JSON: %w", err)
	}
	if err := requireJSONEOF(decoder); err != nil {
		return request{}, err
	}
	if req.SchemaVersion != requestSchemaVersion {
		return request{}, fmt.Errorf("request schema %d is unsupported", req.SchemaVersion)
	}
	if !canonicalTag.MatchString(req.ExpectedTag) {
		return request{}, errors.New("expectedTag is not canonical")
	}
	manifestPath, err := validatePath(req.ManifestPath, manifestName)
	if err != nil {
		return request{}, fmt.Errorf("manifestPath: %w", err)
	}
	bundlePath, err := validatePath(req.BundlePath, bundleName)
	if err != nil {
		return request{}, fmt.Errorf("bundlePath: %w", err)
	}
	if strings.EqualFold(manifestPath, bundlePath) {
		return request{}, errors.New("manifestPath and bundlePath must be different")
	}
	req.ManifestPath = manifestPath
	req.BundlePath = bundlePath
	return req, nil
}

func validatePath(path, expectedName string) (string, error) {
	if path == "" || !filepath.IsAbs(path) {
		return "", errors.New("must be an absolute path")
	}
	cleaned := filepath.Clean(path)
	if filepath.Base(cleaned) != expectedName {
		return "", fmt.Errorf("must name %s", expectedName)
	}
	return cleaned, nil
}

func readBoundedRegularFile(path string, maximum int64) ([]byte, error) {
	info, err := os.Lstat(path)
	if err != nil {
		return nil, err
	}
	if !info.Mode().IsRegular() || info.Mode()&os.ModeSymlink != 0 {
		return nil, errors.New("must be a regular file, not a link or device")
	}
	if info.Size() <= 0 || info.Size() > maximum {
		return nil, fmt.Errorf("size %d is outside the accepted range 1..%d", info.Size(), maximum)
	}
	file, err := os.Open(path)
	if err != nil {
		return nil, err
	}
	defer file.Close()
	openedInfo, err := file.Stat()
	if err != nil {
		return nil, err
	}
	if !openedInfo.Mode().IsRegular() || !os.SameFile(info, openedInfo) {
		return nil, errors.New("file identity changed before it was opened")
	}
	data, err := io.ReadAll(io.LimitReader(file, maximum+1))
	if err != nil {
		return nil, err
	}
	if int64(len(data)) != info.Size() {
		return nil, errors.New("file changed while it was being read")
	}
	return data, nil
}

func checkJSONDocument(data []byte, maximum int) error {
	decoder := json.NewDecoder(bytes.NewReader(data))
	if err := checkJSONValue(decoder, 1, maximum); err != nil {
		return err
	}
	if _, err := decoder.Token(); !errors.Is(err, io.EOF) {
		if err == nil {
			return errors.New("JSON contains trailing content")
		}
		return fmt.Errorf("invalid trailing JSON: %w", err)
	}
	return nil
}

func checkJSONValue(decoder *json.Decoder, depth, maximum int) error {
	if depth > maximum {
		return fmt.Errorf("JSON depth exceeds %d", maximum)
	}
	token, err := decoder.Token()
	if err != nil {
		return fmt.Errorf("invalid JSON: %w", err)
	}
	delimiter, structured := token.(json.Delim)
	if !structured {
		return nil
	}
	switch delimiter {
	case '{':
		seen := make(map[string]struct{})
		for decoder.More() {
			keyToken, err := decoder.Token()
			if err != nil {
				return fmt.Errorf("invalid JSON object: %w", err)
			}
			key, ok := keyToken.(string)
			if !ok {
				return errors.New("JSON object key is not a string")
			}
			if _, duplicate := seen[key]; duplicate {
				return fmt.Errorf("JSON object contains duplicate property %q", key)
			}
			seen[key] = struct{}{}
			if err := checkJSONValue(decoder, depth+1, maximum); err != nil {
				return err
			}
		}
		end, err := decoder.Token()
		if err != nil || end != json.Delim('}') {
			return errors.New("JSON object is not closed")
		}
	case '[':
		for decoder.More() {
			if err := checkJSONValue(decoder, depth+1, maximum); err != nil {
				return err
			}
		}
		end, err := decoder.Token()
		if err != nil || end != json.Delim(']') {
			return errors.New("JSON array is not closed")
		}
	default:
		return errors.New("unexpected JSON delimiter")
	}
	return nil
}

func requireJSONEOF(decoder *json.Decoder) error {
	var trailing any
	if err := decoder.Decode(&trailing); !errors.Is(err, io.EOF) {
		if err == nil {
			return errors.New("request contains trailing JSON")
		}
		return fmt.Errorf("request has invalid trailing content: %w", err)
	}
	return nil
}

func validateResult(result *verify.VerificationResult, expectedRef, expectedSigner string, digest [32]byte) (string, error) {
	if result == nil || result.Signature == nil || result.Signature.Certificate == nil || result.Statement == nil {
		return "", errors.New("verification result is incomplete")
	}
	statement := result.Statement
	if statement.Type != statementType || statement.PredicateType != predicateType {
		return "", errors.New("statement type or predicate type is not accepted")
	}

	predicateJSON, err := protojson.Marshal(statement.Predicate)
	if err != nil {
		return "", fmt.Errorf("provenance predicate: %w", err)
	}
	var predicate provenancePredicate
	decoder := json.NewDecoder(bytes.NewReader(predicateJSON))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&predicate); err != nil {
		return "", fmt.Errorf("provenance predicate is outside the closed schema: %w", err)
	}
	if err := requireJSONEOF(decoder); err != nil {
		return "", err
	}
	workflow := predicate.BuildDefinition.ExternalParameters.Workflow
	github := predicate.BuildDefinition.InternalParameters.GitHub
	if predicate.BuildDefinition.BuildType != buildType || workflow.Ref != expectedRef || workflow.Repository != repositoryURI || workflow.Path != workflowPath {
		return "", errors.New("provenance workflow identity does not match the closed policy")
	}
	if github.EventName != eventName || github.RepositoryID != repositoryID || github.RepositoryOwnerID != ownerID || github.RunnerEnvironment != runnerEnvironment {
		return "", errors.New("provenance GitHub identity does not match the closed policy")
	}
	if predicate.RunDetails.Builder.ID != expectedSigner || !runURI.MatchString(predicate.RunDetails.Metadata.InvocationID) {
		return "", errors.New("provenance run identity does not match the closed policy")
	}
	if len(predicate.BuildDefinition.ResolvedDependencies) != 1 {
		return "", errors.New("provenance must have exactly one resolved source dependency")
	}
	dependency := predicate.BuildDefinition.ResolvedDependencies[0]
	expectedURI := "git+" + repositoryURI + "@" + expectedRef
	commit := dependency.Digest["gitCommit"]
	if dependency.URI != expectedURI || len(dependency.Digest) != 1 || !commitSHA.MatchString(commit) {
		return "", errors.New("provenance resolved source dependency is invalid")
	}
	extensions := result.Signature.Certificate.Extensions
	if extensions.SourceRepositoryDigest != commit || extensions.BuildSignerDigest != commit || extensions.BuildConfigDigest != commit {
		return "", errors.New("certificate commit claims disagree with provenance")
	}
	if extensions.RunInvocationURI != predicate.RunDetails.Metadata.InvocationID {
		return "", errors.New("certificate run invocation disagrees with provenance")
	}
	if len(statement.Subject) != 1 {
		return "", fmt.Errorf("expected exactly one statement subject, found %d", len(statement.Subject))
	}
	subject := statement.Subject[0]
	if subject.Name != manifestName || len(subject.Digest) != 1 || subject.Digest["sha256"] != hex.EncodeToString(digest[:]) {
		return "", errors.New("statement subject does not exactly name and digest the manifest")
	}
	return commit, nil
}
