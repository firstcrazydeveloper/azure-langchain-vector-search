Feature: Vector Search API
  As an application consumer
  I want to perform semantic searches against the indexed document corpus
  So that I can retrieve relevant content based on meaning rather than exact keyword matching

  Background:
    Given the Vector Search API is reachable

  @smoke @search
  Scenario: Semantic search returns results for a valid natural-language query
    When I search for "termination clause europe" with top 3 results
    Then the response status code should be 200
    And the search response should contain at least 1 result
    And each search result should have a non-empty fileName
    And each search result should have a positive relevance score

  @search
  Scenario: Search honours the requested result limit
    When I search for "data retention policy" with top 2 results
    Then the response status code should be 200
    And the search response should contain at most 2 results

  @search
  Scenario Outline: Multiple different domain queries each return relevant results
    When I search for "<query>" with top <topK> results
    Then the response status code should be 200
    And the search response should contain at least 1 result

    Examples:
      | query                        | topK |
      | termination clause europe    | 3    |
      | data retention policy        | 5    |
      | two factor admin security    | 2    |
      | cancel agreement             | 3    |
      | compliance audit findings    | 5    |

  @search
  Scenario: Each search result contains all expected fields
    When I search for "document policy compliance" with top 1 results
    Then the response status code should be 200
    And the search response should contain at least 1 result
    And each search result should have a non-empty chunkId
    And each search result should have a non-empty docType
    And each search result should have a non-empty snippet
    And each search result should have a positive relevance score

  @search @performance
  Scenario: Search responds within the acceptable time threshold
    When I search for "security policy guidelines" with top 5 results
    Then the response status code should be 200
    And the response time should be less than 5000 milliseconds

  @search
  Scenario: Search with top-K of 1 returns at most 1 result
    When I search for "contract terms" with top 1 results
    Then the response status code should be 200
    And the search response should contain at most 1 results
