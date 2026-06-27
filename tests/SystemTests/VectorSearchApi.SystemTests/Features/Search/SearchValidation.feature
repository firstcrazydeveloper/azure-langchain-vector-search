Feature: Search API Input Validation
  As the Vector Search API
  I want to reject or handle malformed search requests gracefully
  So that clients receive clear, actionable error responses

  Background:
    Given the Vector Search API is reachable

  @validation @search
  Scenario: Missing required query parameter returns Unprocessable Entity
    When I send a search request without the query parameter
    Then the response status code should be 422

  @validation @search
  Scenario: Very large top-K value is handled without server error
    When I search for "test policy document" with top 100 results
    Then the response status code should be 200

  @validation @search
  Scenario: Top-K of zero or negative is handled gracefully
    When I search for "test document" with top 0 results
    Then the response status code should not be 500

  @validation @search
  Scenario: Query containing special characters does not cause a server error
    When I search for "contract & termination (clause) 2024" with top 3 results
    Then the response status code should be 200

  @validation @search
  Scenario: Very long query string is handled gracefully
    When I search for "This is a very long search query that tests how the system handles lengthy input strings with many words and technical terms about legal compliance and data retention" with top 5 results
    Then the response status code should not be 500
