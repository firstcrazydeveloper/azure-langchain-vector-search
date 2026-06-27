Feature: Health Check API
  As a system operator
  I want to verify the API health status
  So that I can confirm the Vector Search service is running correctly

  Background:
    Given the Vector Search API is reachable

  @smoke @health
  Scenario: Health endpoint returns a successful OK status
    When I send a GET request to the health check endpoint
    Then the response status code should be 200
    And the health response status field should be "ok"
    And the health response should contain a non-empty index name

  @health
  Scenario: Health endpoint returns the expected index name
    When I send a GET request to the health check endpoint
    Then the response status code should be 200
    And the health response index field should be "docs-index"

  @health @performance
  Scenario: Health endpoint responds within the acceptable time threshold
    When I send a GET request to the health check endpoint
    Then the response status code should be 200
    And the response time should be less than 2000 milliseconds
