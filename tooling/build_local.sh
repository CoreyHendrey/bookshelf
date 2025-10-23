#!/usr/bin/env bash
set -e # Exit immediately if a command exits with a non-zero status.

# --- Configuration ---
# Default to 'softcover' if no argument ($1) is provided
BUILD_VARIANT=${1:-hardcover}
DOCKERFILE_PATH="./docker/Dockerfile"

# --- Get Dynamic Build Args ---
# Replicates the values used in the GHA workflow
GIT_BRANCH=$(git rev-parse --abbrev-ref HEAD)
COMMIT_HASH=$(git rev-parse HEAD)
BUILD_DATE=$(date --rfc-3339=date) # GHA uses --rfc-3339=date

# --- Set Variant-Specific Args ---
if [[ "$BUILD_VARIANT" == "hardcover" ]]; then
    TAG_NAME="readarr-dev:hardcover"
    BUILD_ARGS=(
        "--build-arg" "GIT_BRANCH=$GIT_BRANCH"
        "--build-arg" "COMMIT_HASH=$COMMIT_HASH"
        "--build-arg" "BUILD_DATE=$BUILD_DATE"
        "--build-arg" "METADATA_URL=https://hardcover.bookinfo.pro"
        "--build-arg" "HARDCOVER=true"
    )
elif [[ "$BUILD_VARIANT" == "softcover" ]]; then
    TAG_NAME="readarr-dev:softcover"
    BUILD_ARGS=(
        "--build-arg" "GIT_BRANCH=$GIT_BRANCH"
        "--build-arg" "COMMIT_HASH=$COMMIT_HASH"
        "--build-arg" "BUILD_DATE=$BUILD_DATE"
    )
else
    echo "Error: Unknown build variant '$BUILD_VARIANT'."
    echo "Usage: $0 [softcover|hardcover]"
    exit 1
fi

# --- Run the Build ---
echo "Building $BUILD_VARIANT variant..."
echo "  Tag:          $TAG_NAME"
echo "  Dockerfile:   $DOCKERFILE_PATH"
echo "  Git Branch:   $GIT_BRANCH"
echo "  Commit Hash:  $COMMIT_HASH"
echo "  Build Date:   $BUILD_DATE"
echo "-------------------------------------"

# -f specifies the Dockerfile location
# -t gives it the tag
# "${BUILD_ARGS[@]}" expands the array of build-args safely
# . specifies the build context (the current directory)
docker build -f "$DOCKERFILE_PATH" -t "$TAG_NAME" "${BUILD_ARGS[@]}" .

echo "-------------------------------------"
echo "Build complete for $TAG_NAME"