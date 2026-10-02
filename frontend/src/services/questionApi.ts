import { type AxiosInstance } from 'axios'
import httpClient from './httpClient'
import type { Question, PagedResult, SingleResult, MutationResult } from '../types'

class QuestionAdminApiClient {
  private client: AxiosInstance
  constructor() {
    // Shared Cookie/CSRF client and 401 handling (see httpClient.ts).
    this.client = httpClient
  }
  async search(params: { keyword?: string; level?: number; type?: number; subject: number; grade: number; tagId?: string; page?: number; size?: number }): Promise<PagedResult<Question>> {
    const response = await this.client.get<PagedResult<Question>>('/admin/questions', { params })
    return response.data
  }
  async getById(id: string): Promise<Question> {
    const response = await this.client.get<SingleResult<Question>>(`/admin/questions/${id}`)
    return response.data.data
  }
  async delete(id: string): Promise<MutationResult> {
    const response = await this.client.delete<MutationResult>(`/admin/questions/${id}`)
    return response.data
  }
}

let _instance: QuestionAdminApiClient | null = null
export function getQuestionAdminApiClient(): QuestionAdminApiClient {
  if (!_instance) _instance = new QuestionAdminApiClient()
  return _instance
}
