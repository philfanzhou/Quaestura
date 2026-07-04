export interface PagedResult<T> {
  success: boolean
  data: T[]
  total: number
  page: number
  size: number
  totalPages: number
}

export interface SingleResult<T> {
  success: boolean
  data: T
}

export interface MutationResult {
  success: boolean
  data: { id?: string; deleted?: boolean; taggedCount?: number; removed?: boolean }
}

export interface Knowledge {
  id: string
  parentId: string | null
  name: string
  description: string | null
  createdBy: string | null
  createdAt: string
  isReferenced: boolean
  subject: number
  grade: number
  updatedBy: string | null
  updatedAt: string | null
}

export interface Tag {
  id: string
  name: string
  color: string | null
  description: string | null
  createdBy: string | null
  createdAt: string
  usageCount: number
}

export interface Question {
  id: string
  level: number
  type: number
  width: number
  height: number
  picturePaths: string[]
  content: string
  correctAnswer: string
  analysis: string
  userId: string | null
  studentId: string | null
  mistakeId: string | null
  subject: number
  grade: number
  createdAt: string
  updatedAt: string
}

export interface CreateKnowledgeRequest {
  id?: string
  parentId?: string
  name: string
  description?: string
  subject?: number
  grade?: number
  userId: string
}

export interface CreateTagRequest {
  id?: string
  name: string
  color?: string
  description?: string
  userId: string
}
