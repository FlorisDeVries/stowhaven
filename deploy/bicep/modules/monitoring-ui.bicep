@description('Azure Static Web Apps control-plane region')
param location string

param nameSuffix string
param tags object

// Static hosting only. The browser calls the existing gateway directly; no paid
// linked backend, Functions app, or additional always-on compute is provisioned.
resource site 'Microsoft.Web/staticSites@2024-11-01' = {
  name: 'swa-${nameSuffix}-monitoring'
  location: location
  sku: {
    name: 'Free'
    tier: 'Free'
  }
  properties: {
    provider: 'Custom'
    stagingEnvironmentPolicy: 'Disabled'
    buildProperties: {
      skipGithubActionWorkflowGeneration: true
    }
  }
  tags: tags
}

output name string = site.name
output origin string = 'https://${site.properties.defaultHostname}'
