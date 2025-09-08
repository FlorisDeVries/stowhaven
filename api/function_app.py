import datetime
import os
import azure.functions as func

from azure.storage.blob import (
    BlobSasPermissions,
    generate_blob_sas,
    BlobServiceClient
)

app = func.FunctionApp(http_auth_level=func.AuthLevel.FUNCTION)

DATA_SA = os.environ["DATA_STORAGE_ACCOUNT"]
DATA_CONTAINER = os.environ.get("DATA_CONTAINER", "backups")

def _get_udk(blob_service_client):
    now = datetime.datetime.now(datetime.timezone.utc)
    # UDK valid for short period
    return blob_service_client.get_user_delegation_key(key_start_time=now - datetime.timedelta(minutes=5),
                                                       key_expiry_time=now + datetime.timedelta(hours=2))

def _sas_url(path: str, perms: BlobSasPermissions, ttl_minutes: int) -> str:
    ttl = max(1, min(ttl_minutes, 240))
    expiry = datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(minutes=ttl)

    # Note: for user delegation SAS we use credential=None in generate_blob_sas
    # and provide user_delegation_key later.
    bsc = BlobServiceClient(account_url=f"https://{DATA_SA}.blob.core.windows.net")
    udk = _get_udk(bsc)

    sas = generate_blob_sas(
        account_name=DATA_SA,
        container_name=DATA_CONTAINER,
        blob_name=path,
        user_delegation_key=udk,
        permission=perms,
        expiry=expiry
    )
    return f"https://{DATA_SA}.blob.core.windows.net/{DATA_CONTAINER}/{path}?{sas}"

@app.function_name(name="get_sas_upload")
@app.route(route="get-sas-upload", methods=["POST"])
def get_sas_upload(req: func.HttpRequest) -> func.HttpResponse:
    # Simple key-auth: set 'x-api-key' as Function key or as App Setting and check it here.
    api_key = req.headers.get("x-api-key") or req.params.get("code")  # Functions key compat
    functions_key = os.environ.get("FUNCTIONS_WORKER_RUNTIME")  # dummy read to avoid linter
    # In production: replace with real check, e.g. against req.headers['x-api-key'] == os.environ['MY_API_KEY']

    data = req.get_json()
    rel_path = data.get("path")
    ttl = int(data.get("ttl_minutes", 60))
    if not rel_path:
        return func.HttpResponse("Missing 'path'", status_code=400)

    perms = BlobSasPermissions(create=True, write=True, add=True)
    url = _sas_url(rel_path, perms, ttl)
    return func.HttpResponse(f'{{"sas_url":"{url}"}}', mimetype="application/json")

@app.function_name(name="get_sas_download")
@app.route(route="get-sas-download", methods=["POST"])
def get_sas_download(req: func.HttpRequest) -> func.HttpResponse:
    data = req.get_json()
    rel_path = data.get("path")
    ttl = int(data.get("ttl_minutes", 60))
    if not rel_path:
        return func.HttpResponse("Missing 'path'", status_code=400)

    perms = BlobSasPermissions(read=True)
    url = _sas_url(rel_path, perms, ttl)
    return func.HttpResponse(f'{{"sas_url":"{url}"}}', mimetype="application/json")
