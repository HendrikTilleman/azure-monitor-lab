@description('Resource ID of the lab Application Insights component that emits portal.order.failed.')
param appInsightsId string

@description('Resource ID of the existing lab action group.')
param actionGroupId string

@description('Resource tags.')
param tags object = {}

resource ordersSloBurn 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: 'alert-orders-slo-burn'
  location: 'global'
  tags: tags
  properties: {
    description: 'Orders slo-burn: portal.order.failed average exceeds 0.05 over 5 minutes.'
    severity: 1
    enabled: true
    scopes: [appInsightsId]
    evaluationFrequency: 'PT1M'
    windowSize: 'PT5M'
    autoMitigate: true
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          name: 'PortalOrderFailureRate'
          metricNamespace: 'azure.applicationinsights'
          metricName: 'portal.order.failed'
          operator: 'GreaterThan'
          threshold: json('0.05')
          timeAggregation: 'Average'
          criterionType: 'StaticThresholdCriterion'
          skipMetricValidation: true
        }
      ]
    }
    actions: [
      { actionGroupId: actionGroupId }
    ]
  }
}

output alertId string = ordersSloBurn.id
