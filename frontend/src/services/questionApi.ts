import httpClient from './httpClient'
import type { Question, PagedResult, SingleResult, MutationResult } from '../types'

export const questionApi = {
  async search(params: { keyword?: string; level?: number; type?: number; subject: number; grade: number; tagId?: string; page?: number; size?: number }): Promise<PagedResult<Question>> {
    const response = await httpClient.get<PagedResult<Question>>('/admin/questions', { params })
    return response.data
  },
  async getById(id: string): Promise<Question> {
    const response = await httpClient.get<SingleResult<Question>>(`/admin/questions/${id}`)
    return response.data.data
  },
  async delete(id: string): Promise<MutationResult> {
    const response = await httpClient.delete<MutationResult>(`/admin/questions/${id}`)
    return response.data
  },
}
